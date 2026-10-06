using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Pidge.Core;

namespace Pidge.HttpEngine;

public sealed partial class HttpEngine
{
    private const int ChunkSize = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Headers that describe the body rather than the request, and so travel with it.</summary>
    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Content-Type",
        "Expires",
        "Last-Modified",
    };

    private TlsConfig _tls = null!;
    private HttpMessageInvoker _invoker = null!;
    private readonly CookieContainer _cookies = new();

    /// <summary>
    /// The HTTP/2 connection to each origin. One connection carries every
    /// request to its origin and writes to it from a loop of its own, so a
    /// request cannot mark it the way it marks an HTTP/1.1 connection.
    /// </summary>
    private readonly ConcurrentDictionary<string, TrackedStream> _http2Connections = new();

    /// <summary>
    /// OAuth 2 access tokens, kept until they expire so a burst of requests
    /// costs one token request rather than one each.
    /// </summary>
    private readonly TokenCache _tokens = new();

    partial void Initialize()
    {
        _tls = TlsConfig.Load(Config.Tls);

        SocketsHttpHandler handler;
        try
        {
            handler = CreateHandler(ntlm: false);
        }
        catch (Exception err) when (err is not RequestErrorException)
        {
            // A bad certificate surfaces when the handler is built rather than
            // where it was loaded. If TLS was configured at all, that is the
            // cause worth pointing at.
            throw (Config.Tls.IsDefault
                    ? new RequestError(RequestErrorKind.Other, "Could not start the HTTP client.")
                    : new RequestError(
                        RequestErrorKind.Tls,
                        "Could not apply the certificate settings. Check that each file is a valid certificate."))
                .WithDetail(Errors.Chain(err))
                .AsException();
        }

        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public void Dispose() => _invoker.Dispose();

    /// <summary>
    /// The transport. Redirects are followed here rather than by the handler,
    /// so the rules for what survives a redirect are ours, and each hop is
    /// counted against the configured limit.
    /// </summary>
    private SocketsHttpHandler CreateHandler(bool ntlm)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = Config.StoreCookies,
            // Header bytes are read as they are, so a value that is not text can
            // be reported as such rather than guessed at.
            ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
            SslOptions = _tls.ClientOptions(),
            // Puts the peer certificate on the response, for the padlock.
            PlaintextStreamFilter = (context, _) => ValueTask.FromResult<Stream>(Track(context)),
        };
        if (Config.StoreCookies)
        {
            handler.CookieContainer = ntlm ? new CookieContainer() : _cookies;
        }
        if (ntlm)
        {
            handler.MaxConnectionsPerServer = 1;
        }
        return handler;
    }

    private TrackedStream Track(SocketsHttpPlaintextStreamFilterContext context)
    {
        var stream = new TrackedStream(context.PlaintextStream);
        if (context.NegotiatedHttpVersion.Major >= 2 && context.InitialRequestMessage.RequestUri is { } uri)
        {
            _http2Connections[Origin(uri)] = stream;
        }
        return stream;
    }

    private static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";

    private async partial Task<HttpResponse> ExecuteCoreAsync(HttpRequest request, CancellationHandle cancellation)
    {
        var timeoutMs = request.TimeoutMs ?? Config.DefaultTimeout;
        var started = Stopwatch.GetTimestamp();

        // The timeout covers the whole exchange, including a digest retry: two
        // round trips the user did not ask for should not buy twice the wait.
        using var timeout = new CancellationTokenSource();
        if (timeoutMs > 0)
        {
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs, (ulong)int.MaxValue)));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation.Token);

        try
        {
            return await SendAsync(request, started, linked.Token).ConfigureAwait(false);
        }
        catch (Exception err) when (err is not RequestErrorException && linked.IsCancellationRequested)
        {
            throw (timeout.IsCancellationRequested && !cancellation.IsCancelled
                    ? RequestError.Timeout(timeoutMs)
                    : RequestError.Cancelled())
                .AsException();
        }
        catch (Exception err) when (err is not RequestErrorException)
        {
            throw Errors.FromHttp(err, null, isBody: false).AsException();
        }
    }

    /// <summary>One request, or two when the server answers a digest challenge.</summary>
    private async Task<HttpResponse> SendAsync(HttpRequest request, long started, CancellationToken cancellationToken)
    {
        var prepared = Prepare.Request(request);
        var warnings = prepared.Warnings;

        // OAuth 2 needs a token before anything can be sent, and a failure to
        // get one is an error rather than a response: the request never left.
        if (request.Auth is OAuth2Settings settings && request.FindHeader("authorization") is null)
        {
            var token = await OAuth2.AccessTokenAsync(settings, _tokens, SendTokenRequestAsync, cancellationToken)
                .ConfigureAwait(false);
            prepared.Headers.Add((RequestPlanning.Authorization, $"Bearer {token}"));
        }

        var response = await RunAsync(_invoker, prepared, Config.FollowRedirects, started, cancellationToken)
            .ConfigureAwait(false);
        response.Warnings = [.. warnings];

        // 401 is the only status that carries a challenge, and a header the
        // user typed has already won.
        if (response.Status != 401 || request.FindHeader("authorization") is not null)
        {
            return response;
        }

        switch (request.Auth)
        {
            case DigestAuth digest:
                {
                    var header = Digest.Answer(request, response, digest.Username, digest.Password, out var why);
                    if (header is null)
                    {
                        // The 401 is the honest answer; the note says why there was
                        // no second attempt.
                        response.Warnings.Add(why ?? "");
                        return response;
                    }

                    var answered = await SendWithAuthorizationAsync(
                            _invoker, request, header, Config.FollowRedirects, started, cancellationToken)
                        .ConfigureAwait(false);
                    answered.Warnings = [.. warnings];
                    return answered;
                }

            case NtlmAuth ntlm:
                {
                    var (answered, why) = await NtlmHandshakeAsync(request, response, ntlm, started, cancellationToken)
                        .ConfigureAwait(false);
                    if (why is not null)
                    {
                        response.Warnings.Add(why);
                        return response;
                    }
                    if (answered is null)
                    {
                        return response;
                    }
                    answered.Warnings = [.. warnings];
                    return answered;
                }

            default:
                return response;
        }
    }

    /// <summary>
    /// The three legs of NTLM, on a connection kept to this exchange.
    ///
    /// NTLM authenticates a connection rather than a request, so all three
    /// messages have to travel the same socket. Nothing in the shared pool pins
    /// one, so this builds a handler that holds a single connection and is used
    /// by nothing else: the negotiate and authenticate messages go out back to
    /// back, and the idle connection the first leg returns is the only one the
    /// second can take.
    ///
    /// Neither an answer nor a reason means the server offered something other
    /// than NTLM, in which case its 401 stands as the answer.
    /// </summary>
    private async Task<(HttpResponse? Answered, string? Why)> NtlmHandshakeAsync(
        HttpRequest request,
        HttpResponse challenged,
        NtlmAuth ntlm,
        long started,
        CancellationToken cancellationToken)
    {
        var offered = challenged.Header("www-authenticate") ?? "";
        if (!offered.Split(',').Any(scheme => Digest.AsciiLower(scheme.Trim()).StartsWith("ntlm", StringComparison.Ordinal)))
        {
            return (null, null);
        }

        HttpMessageInvoker client;
        try
        {
            client = new HttpMessageInvoker(CreateHandler(ntlm: true), disposeHandler: true);
        }
        catch (Exception err)
        {
            var error = new RequestError(RequestErrorKind.Other, "Could not start the NTLM client.")
                .WithDetail(Errors.Chain(err));
            return (null, $"An NTLM connection could not be opened: {error.Message}.");
        }

        using (client)
        {
            // Leg one: what the client can do. The server answers 401 again,
            // this time with its challenge. Redirects would start a new
            // connection mid-handshake, so none are followed.
            HttpResponse negotiated;
            try
            {
                negotiated = await SendWithAuthorizationAsync(
                        client,
                        request,
                        Ntlm.NegotiateHeader(ntlm.Domain, ntlm.Workstation),
                        followRedirects: false,
                        started,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RequestErrorException err)
            {
                return (null, $"The NTLM negotiation failed: {err.Error.Message}.");
            }

            if (negotiated.Header("www-authenticate") is not { } challengeHeader)
            {
                return (null, "The server did not answer the NTLM negotiation.");
            }

            Ntlm.Challenge challenge;
            try
            {
                challenge = Ntlm.ParseChallenge(challengeHeader);
            }
            catch (Ntlm.ChallengeException err)
            {
                return (null, err.Message);
            }

            // Leg two: the response computed from that challenge.
            try
            {
                var answered = await SendWithAuthorizationAsync(
                        client,
                        request,
                        Ntlm.AuthenticateHeader(
                            challenge,
                            ntlm.Username,
                            ntlm.Password,
                            ntlm.Domain,
                            ntlm.Workstation,
                            Ntlm.ClientChallenge(),
                            Ntlm.Timestamp()),
                        followRedirects: false,
                        started,
                        cancellationToken)
                    .ConfigureAwait(false);
                return (answered, null);
            }
            catch (RequestErrorException err)
            {
                return (null, $"The NTLM authentication failed: {err.Error.Message}.");
            }
        }
    }

    /// <summary>
    /// Sends the request again with an <c>Authorization</c> header the engine
    /// has computed, optionally on a client of its own.
    /// </summary>
    private async Task<HttpResponse> SendWithAuthorizationAsync(
        HttpMessageInvoker client,
        HttpRequest request,
        string header,
        bool followRedirects,
        long started,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare.Request(request);
        prepared.SetHeader(RequestPlanning.Authorization, header);
        return await RunAsync(client, prepared, followRedirects, started, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The token endpoint's POST, on the engine's own client.</summary>
    private async Task<OAuth2.TokenReply> SendTokenRequestAsync(
        string tokenUrl,
        List<(string Name, string Value)> headers,
        string form,
        CancellationToken cancellationToken)
    {
        if (!WebUrl.TryParse(tokenUrl, out var url, out var error))
        {
            throw BuilderError($"builder error: {error}").AsException();
        }
        if (url.Scheme is not ("http" or "https"))
        {
            throw BuilderError($"builder error for url ({url}): URL scheme is not allowed").AsException();
        }

        var prepared = new PreparedRequest
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(form),
        };
        var response = await RunAsync(_invoker, prepared, Config.FollowRedirects, Stopwatch.GetTimestamp(), cancellationToken)
            .ConfigureAwait(false);
        return new OAuth2.TokenReply(response.Status, Encoding.UTF8.GetString(response.Body));
    }

    private static RequestError BuilderError(string detail) =>
        new RequestError(RequestErrorKind.BodySerialization, "The request could not be built.").WithDetail(detail);

    /// <summary>Sends, follows redirects if asked to, and reads the final response.</summary>
    private async Task<HttpResponse> RunAsync(
        HttpMessageInvoker client,
        PreparedRequest prepared,
        bool followRedirects,
        long started,
        CancellationToken cancellationToken)
    {
        var current = prepared.Clone();
        var visited = new List<WebUrl>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var capture = new ConnectionCapture();
            ConnectionCapture.Current = capture;

            // The NTLM client authenticates the one connection it has, which
            // HTTP/2 does not allow; everything else uses it where offered.
            using var message = ToMessage(current, http2: client == _invoker);
            HttpResponseMessage reply;
            try
            {
                reply = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception err) when (!cancellationToken.IsCancellationRequested)
            {
                throw Errors.FromHttp(err, current.Url, isBody: false).AsException();
            }

            if (capture.Connection is null
                && reply.Version.Major >= 2
                && _http2Connections.TryGetValue(Origin(message.RequestUri!), out var shared))
            {
                capture.Connection = shared;
            }

            using (reply)
            {
                var status = (int)reply.StatusCode;
                if (followRedirects && status is 301 or 302 or 303 or 307 or 308 && Location(reply, current.Url) is { } next)
                {
                    visited.Add(current.Url);
                    if (visited.Count > Config.MaxRedirects)
                    {
                        throw new RequestError(RequestErrorKind.TooManyRedirects, "The server redirected too many times.")
                            .WithDetail($"error following redirect for url ({current.Url}): too many redirects")
                            .AsException();
                    }
                    if (next.Scheme is not ("http" or "https"))
                    {
                        throw BuilderError($"builder error for url ({next}): URL scheme is not allowed").AsException();
                    }

                    Redirect(current, status, next);
                    continue;
                }

                return await ReadResponseAsync(reply, current.Url, capture, started, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>What a browser carries across a redirect, and what it drops.</summary>
    private static void Redirect(PreparedRequest current, int status, WebUrl next)
    {
        if (status is 301 or 302 or 303)
        {
            // The body is not replayed, and nor is anything that described it.
            current.Body = null;
            foreach (var name in (string[])["Transfer-Encoding", "Content-Encoding", "Content-Type", "Content-Length"])
            {
                current.RemoveHeader(name);
            }
            if (current.Method is not ("GET" or "HEAD"))
            {
                current.Method = "GET";
            }
        }

        // Credentials are for the host they were typed for.
        if (next.Host != current.Url.Host || next.PortOrKnownDefault != current.Url.PortOrKnownDefault)
        {
            foreach (var name in (string[])["Authorization", "Cookie", "Cookie2", "Proxy-Authorization", "WWW-Authenticate"])
            {
                current.RemoveHeader(name);
            }
        }

        // The page we came from, minus credentials and fragment, unless that
        // would leak an https address to plain http.
        if (!(next.Scheme == "http" && current.Url.Scheme == "https"))
        {
            var referer = Prepare.WithoutCredentials(current.Url);
            referer.SetFragment(null);
            current.SetHeader("Referer", referer.ToString());
        }

        current.Url = next;
    }

    /// <summary>
    /// Where a redirect points, resolved against the URL that was requested.
    /// Null when there is no Location or it cannot be made sense of, in which
    /// case the redirect itself is the answer.
    /// </summary>
    private static WebUrl? Location(HttpResponseMessage reply, WebUrl current)
    {
        if (!reply.Headers.NonValidated.TryGetValues("Location", out var values))
        {
            return null;
        }

        string? raw = null;
        foreach (var value in values)
        {
            raw = value;
            break;
        }
        if (raw is null)
        {
            return null;
        }

        // Some sites send UTF-8 here, although the bytes are meant to be opaque.
        try
        {
            raw = StrictUtf8.GetString(Encoding.Latin1.GetBytes(raw));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        // An absolute Location is taken as written.
        if (WebUrl.TryParse(raw.Trim(), out var absolute, out _))
        {
            return absolute;
        }

        if (!Uri.TryCreate(current.ToString(), UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, raw.Trim(), out var resolved))
        {
            return null;
        }

        return WebUrl.TryParse(resolved.AbsoluteUri, out var next, out _) ? next : null;
    }

    /// <summary>
    /// The URL as sent. <see cref="Uri"/> would otherwise rewrite the path and
    /// query on its way to the wire, decoding <c>%41</c> and escaping <c>|</c>,
    /// and the server is meant to see what the URL bar shows.
    /// </summary>
    private static Uri Target(WebUrl url)
    {
        var target = url.Clone();
        target.SetFragment(null);
        return new Uri(target.ToString(), new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private HttpRequestMessage ToMessage(PreparedRequest prepared, bool http2)
    {
        var message = new HttpRequestMessage(new HttpMethod(prepared.Method), Target(prepared.Url))
        {
            // HTTP/2 when a server offers it over TLS, HTTP/1.1 otherwise.
            Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = http2 ? HttpVersionPolicy.RequestVersionOrLower : HttpVersionPolicy.RequestVersionExact,
        };

        HttpContent? content = prepared.Body is null ? null : new ByteArrayContent(prepared.Body);
        foreach (var (name, value) in prepared.Headers)
        {
            // The transport writes the length of what it actually sends.
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (ContentHeaders.Contains(name))
            {
                content ??= new ByteArrayContent([]);
                content.Headers.TryAddWithoutValidation(name, value);
                continue;
            }
            message.Headers.TryAddWithoutValidation(name, value);
        }
        message.Content = content;

        if (!message.Headers.Contains("Accept"))
        {
            message.Headers.TryAddWithoutValidation("Accept", "*/*");
        }
        if (!message.Headers.Contains("User-Agent"))
        {
            message.Headers.TryAddWithoutValidation("User-Agent", Config.UserAgent);
        }

        return message;
    }

    private async Task<HttpResponse> ReadResponseAsync(
        HttpResponseMessage reply,
        WebUrl url,
        ConnectionCapture capture,
        long started,
        CancellationToken cancellationToken)
    {
        var tls = capture.Tls();

        var headers = new List<KeyValueEntry>();
        string? mimeType = null;
        foreach (var header in reply.Headers.NonValidated)
        {
            CollectHeader(headers, header.Key, header.Value, ref mimeType);
        }
        foreach (var header in reply.Content.Headers.NonValidated)
        {
            CollectHeader(headers, header.Key, header.Value, ref mimeType);
        }

        var (body, truncated) = await ReadBodyAsync(reply, url, cancellationToken).ConfigureAwait(false);

        var status = (int)reply.StatusCode;
        return new HttpResponse
        {
            Status = (ushort)status,
            StatusText = StatusReasons.Canonical(status),
            Headers = headers,
            Body = body,
            MimeType = mimeType,
            DurationMs = (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            SizeBytes = (ulong)body.Length,
            Truncated = truncated,
            FinalUrl = url.ToString(),
            Warnings = [],
            Tls = tls,
        };
    }

    /// <summary>
    /// Names are lower case, as HTTP/2 writes them. A value that is not
    /// visible ASCII is shown by its size rather than guessed at.
    /// </summary>
    private static void CollectHeader(
        List<KeyValueEntry> headers,
        string name,
        System.Net.Http.Headers.HeaderStringValues values,
        ref string? mimeType)
    {
        var lowered = Digest.AsciiLower(name);
        foreach (var value in values)
        {
            var visible = value.All(c => c == '\t' || c is >= ' ' and <= '~');
            if (visible && mimeType is null && lowered == "content-type")
            {
                mimeType = value;
            }
            headers.Add(new KeyValueEntry(lowered, visible ? value : $"<{value.Length} bytes>"));
        }
    }

    /// <summary>
    /// Streams the body so a huge response is cut off instead of buffered whole,
    /// and so cancellation lands mid-download rather than only before send.
    /// </summary>
    private async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage reply,
        WebUrl url,
        CancellationToken cancellationToken)
    {
        var limit = Config.MaxResponseBytes;
        using var body = new MemoryStream();
        var truncated = false;

        try
        {
            var stream = await reply.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[ChunkSize];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if ((ulong)body.Length + (ulong)read > limit)
                    {
                        var room = (int)(limit - (ulong)body.Length);
                        body.Write(buffer, 0, room);
                        truncated = true;
                        break;
                    }
                    body.Write(buffer, 0, read);
                }
            }
        }
        catch (Exception err) when (!cancellationToken.IsCancellationRequested)
        {
            throw Errors.FromHttp(err, url, isBody: true).AsException();
        }

        return (body.ToArray(), truncated);
    }
}
