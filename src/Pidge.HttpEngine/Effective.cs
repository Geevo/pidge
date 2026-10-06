using System.Text;
using System.Text.Json;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// What the Auth tab contributes to a request, worked out before anything is
/// sent.
///
/// This exists as a value rather than as a step in building the request
/// because the code generator has to describe the same decisions in many
/// languages, and a rule that lived only inside the sender would be
/// reimplemented there — and would drift from what Send actually does.
/// </summary>
public abstract record AuthPlan
{
    /// <summary>No auth, or a header the user set themselves.</summary>
    public sealed record Nothing : AuthPlan;

    /// <summary>One header, complete and ready to send.</summary>
    public sealed record Header(string Name, string Value) : AuthPlan;

    /// <summary>An API key in the query string, already folded into the URL.</summary>
    public sealed record QueryParam : AuthPlan;

    /// <summary>Answered when the server challenges.</summary>
    public sealed record Challenge(ChallengeAuth Auth) : AuthPlan;

    /// <summary>Signed at send time, over the final URL and body.</summary>
    public sealed record OAuth1(OAuth1Settings Settings) : AuthPlan;

    /// <summary>A token is fetched from the endpoint before the request goes out.</summary>
    public sealed record OAuth2(string TokenUrl) : AuthPlan;
}

/// <summary>The two schemes that begin with a 401 and a second request.</summary>
public abstract record ChallengeAuth
{
    public sealed record Digest(string Username, string Password) : ChallengeAuth;

    public sealed record Ntlm(string Username, string Password, string Domain, string Workstation) : ChallengeAuth;
}

/// <summary>
/// A request worked out as far as it can be without a network: the URL that
/// will be requested, the headers that will go with it, and whatever the Auth
/// tab still owes.
///
/// The engine and the code generator are both built on this, so a snippet the
/// user copies makes the same request the Send button does.
/// </summary>
public sealed record EffectiveRequest(
    /// Normalized, with the query rows and a query-placed API key folded in.
    string Url,
    /// The user's rows first, then the auth header, then the content type the
    /// body implies. Order and letter case are as typed.
    IReadOnlyList<(string Name, string Value)> Headers,
    /// What still has to happen at send time: a challenge, a signature, a token.
    AuthPlan Auth,
    /// The same notes the response pane shows after a send.
    IReadOnlyList<string> Warnings);

public static class RequestPlanning
{
    public const string Authorization = "Authorization";

    /// <summary>
    /// Works a request out without sending it. The only failure is a URL that
    /// cannot be parsed, which is the one thing nothing downstream can describe.
    /// </summary>
    /// <exception cref="RequestErrorException">The URL is not usable.</exception>
    public static EffectiveRequest Effective(HttpRequest request)
    {
        var url = UrlInput.NormalizeUrl(request.Url);
        AppendQueryParams(url, request);

        var warnings = new List<string>();
        var userContentType = request.FindHeader("content-type") is not null;
        var multipart = request.Body is MultipartBody;

        var headers = request.Headers
            .Where(entry => entry.IsActive)
            // Multipart writes its own, so the user's row is not what goes out.
            .Where(entry => !(multipart && entry.Name.Trim().Equals("content-type", StringComparison.OrdinalIgnoreCase)))
            .Select(entry => (entry.Name.Trim(), entry.Value))
            .ToList();

        var auth = PlanAuth(request, warnings);
        if (auth is AuthPlan.Header header)
        {
            headers.Add((header.Name, header.Value));
        }

        if (ContentTypeFor(request.Body, userContentType, warnings) is { } contentType)
        {
            headers.Add(("Content-Type", contentType));
        }

        return new EffectiveRequest(url.ToString(), headers, auth, warnings);
    }

    /// <summary>
    /// Folds the query table, and an API key placed in the query, into the URL.
    ///
    /// A row that the URL already carries is not appended again: the two are
    /// one thing shown twice, since editing the table rewrites the URL's query
    /// and typing a query in the URL fills the table. The comparison is on
    /// decoded pairs, so it holds however either side spelt the encoding. Only
    /// the URL as it arrived is consulted: two identical rows still send two
    /// copies, because that is what the table says.
    ///
    /// Built by hand rather than as a form, because a form encodes a space as
    /// <c>+</c>, which a server is entitled to read as a plus; and the query
    /// already in the URL is the user's own text, carried across untouched.
    /// </summary>
    public static void AppendQueryParams(WebUrl url, HttpRequest request)
    {
        var inUrl = url.QueryPairs().ToHashSet();

        var active = request.QueryParams
            .Where(entry => entry.IsActive)
            .Where(entry => !inUrl.Contains((entry.Name.Trim(), entry.Value)))
            .Select(entry => (Name: entry.Name.Trim(), entry.Value))
            .ToList();

        // An API key placed in the query string is a query param like any other.
        if (request.Auth is ApiKeyAuth { Placement: ApiKeyPlacement.Query } apiKey && apiKey.Key.Trim().Length > 0)
        {
            active.Add((apiKey.Key.Trim(), apiKey.Value));
        }

        if (active.Count == 0)
        {
            return;
        }

        var query = new StringBuilder(url.Query ?? "");
        foreach (var (name, value) in active)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }
            query.Append(MaybeEncode(name, request.EncodeQuery));
            query.Append('=');
            query.Append(MaybeEncode(value, request.EncodeQuery));
        }

        url.SetQuery(query.ToString());
    }

    /// <summary>Everything but RFC 3986's unreserved set, which is what <c>encodeURIComponent</c> and curl both produce.</summary>
    public static string EncodeQueryComponent(string value) => PercentEncoding.Encode(value, EncodeSet.Unreserved);

    /// <summary>
    /// With encoding off the text goes in as typed; the URL still escapes what
    /// cannot appear in a query at all, such as a space, so the result is valid.
    /// </summary>
    private static string MaybeEncode(string value, bool encodeQuery) =>
        encodeQuery ? EncodeQueryComponent(value) : value;

    /// <summary>
    /// A header the user typed always wins; they typed it on purpose. The auth
    /// helper is skipped and the conflict is reported, never silently resolved.
    ///
    /// Which header that is depends on the scheme: <c>Authorization</c> for
    /// bearer and basic, but an API key names its own, and a key in the query
    /// string collides with nothing.
    /// </summary>
    public static AuthPlan PlanAuth(HttpRequest request, List<string> warnings)
    {
        bool Occupied(string name) => request.FindHeader(name) is not null;

        switch (request.Auth)
        {
            case NoAuth:
                return new AuthPlan.Nothing();

            case BearerAuth or BasicAuth or DigestAuth or NtlmAuth or OAuth1Settings or OAuth2Settings
                when Occupied("authorization"):
                warnings.Add("An explicit Authorization header is set, so the Auth tab was ignored.");
                return new AuthPlan.Nothing();

            case BearerAuth bearer:
                return new AuthPlan.Header(Authorization, $"Bearer {bearer.Token}");
            case BasicAuth basic:
                return new AuthPlan.Header(Authorization, BasicCredentials(basic.Username, basic.Password));

            case DigestAuth digest:
                return new AuthPlan.Challenge(new ChallengeAuth.Digest(digest.Username, digest.Password));
            case NtlmAuth ntlm:
                return new AuthPlan.Challenge(
                    new ChallengeAuth.Ntlm(ntlm.Username, ntlm.Password, ntlm.Domain, ntlm.Workstation));

            case OAuth1Settings settings:
                return new AuthPlan.OAuth1(settings.CloneSettings());
            case OAuth2Settings settings:
                return new AuthPlan.OAuth2(settings.TokenUrl);

            // The query placement is handled with the other query params.
            case ApiKeyAuth { Placement: ApiKeyPlacement.Query }:
                return new AuthPlan.QueryParam();
            case ApiKeyAuth key when key.Key.Trim().Length == 0:
                return new AuthPlan.Nothing();
            case ApiKeyAuth key when Occupied(key.Key.Trim()):
                warnings.Add($"An explicit {key.Key.Trim()} header is set, so the Auth tab was ignored.");
                return new AuthPlan.Nothing();
            case ApiKeyAuth key:
                return new AuthPlan.Header(key.Key.Trim(), key.Value);

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Auth.GetType().Name);
        }
    }

    /// <summary>
    /// The <c>Authorization</c> value for a username and password, as every
    /// client spells it: the pair joined by a colon and base64'd.
    /// </summary>
    public static string BasicCredentials(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    /// <summary>
    /// The Content-Type the engine will send for a body.
    ///
    /// Null means nothing is set here: there is no body, the user named the
    /// type themselves, or the transport writes its own — multipart's carries
    /// the boundary it generated, so nobody else may write that header.
    /// </summary>
    public static string? ContentTypeFor(RequestBody body, bool userContentType, List<string> warnings)
    {
        if (body is MultipartBody)
        {
            if (userContentType)
            {
                warnings.Add("Multipart sets its own Content-Type with a boundary; the header you set was replaced.");
            }
            return null;
        }

        if (userContentType)
        {
            return null;
        }

        return body switch
        {
            JsonBody => "application/json",
            TextBody text => text.ContentType ?? "text/plain; charset=utf-8",
            UrlEncodedBody => "application/x-www-form-urlencoded",
            _ => null,
        };
    }

    /// <summary>An <c>application/x-www-form-urlencoded</c> body.</summary>
    public static string FormBody(IEnumerable<KeyValueEntry> entries) =>
        PercentEncoding.FormSerialize(entries.Where(e => e.IsActive).Select(e => (e.Name.Trim(), e.Value)));

    /// <summary>
    /// Refuses a JSON body that does not parse, so a typo is caught here rather
    /// than sent as <c>application/json</c> for the server to reject. The text
    /// is sent as typed, not re-serialized: parsing only checks it. A blank
    /// body is let through, as it is for every other body type.
    /// </summary>
    /// <exception cref="RequestErrorException">The text is not JSON.</exception>
    public static void CheckJson(string text)
    {
        if (text.Trim().Length == 0)
        {
            return;
        }
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException err)
        {
            var line = (err.LineNumber ?? 0) + 1;
            var column = (err.BytePositionInLine ?? 0) + 1;
            throw new RequestError(
                    RequestErrorKind.BodySerialization,
                    $"The JSON body is not valid JSON (line {line}, column {column}).")
                .WithDetail($"{StripJsonExceptionLocation(err.Message)} at line {line} column {column}")
                .AsException();
        }
    }

    private static string StripJsonExceptionLocation(string message)
    {
        var index = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (index >= 0 ? message[..index] : message).TrimEnd('.', ' ');
    }

    /// <summary>
    /// Whether <paramref name="name"/> is an HTTP token (RFC 9110), which is
    /// what a header name has to be.
    /// </summary>
    public static bool IsValidHeaderName(string name) =>
        name.Length > 0 && name.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)));

    /// <summary>
    /// Whether <paramref name="value"/> can be a header value: no control
    /// characters other than a tab.
    /// </summary>
    public static bool IsValidHeaderValue(string value) =>
        value.All(c => c == '\t' || (c >= 32 && c != 127));

    /// <summary>
    /// The active header rows, checked, in order and as typed.
    /// </summary>
    /// <exception cref="RequestErrorException">A name or value cannot be sent.</exception>
    public static List<(string Name, string Value)> BuildHeaders(HttpRequest request)
    {
        var headers = new List<(string, string)>();
        foreach (var entry in request.Headers.Where(e => e.IsActive))
        {
            var name = entry.Name.Trim();
            if (!IsValidHeaderName(name))
            {
                throw new RequestError(RequestErrorKind.InvalidHeader, $"`{name}` is not a valid header name.")
                    .WithDetail("invalid HTTP header name")
                    .AsException();
            }
            if (!IsValidHeaderValue(entry.Value))
            {
                throw new RequestError(
                        RequestErrorKind.InvalidHeader,
                        $"The value for `{name}` is not a valid header value.")
                    .WithDetail("failed to parse header value")
                    .AsException();
            }
            headers.Add((name, entry.Value));
        }
        return headers;
    }
}
