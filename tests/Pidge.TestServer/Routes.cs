using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pidge.TestServer;

internal static class Routes
{
    /// <summary>Answers one request. True means the connection stays open for another.</summary>
    public static async Task<bool> DispatchAsync(
        Request request,
        Stream stream,
        ConnectionState state,
        CancellationToken cancellationToken)
    {
        var segments = request.Segments();

        // Streaming routes write their own bytes, so they are handled first.
        switch (segments)
        {
            case ["slow-body", var chunks, var delayMs]:
                await SlowBodyAsync(stream, Parse(chunks, 5), Parse(delayMs, 100), cancellationToken).ConfigureAwait(false);
                return false;
            case ["never"]:
                // Hold the connection open with no response at all.
                await Task.Delay(TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
                return false;
        }

        // Keep-alive unless the client asked to close.
        var connection = request.Header("connection")?.ToLowerInvariant();
        var keepAlive = (connection?.Contains("keep-alive", StringComparison.Ordinal) ?? false)
            || !(connection?.Contains("close", StringComparison.Ordinal) ?? false);

        var response = segments is ["ntlm"]
            ? NtlmRoute(request, state)
            : await RouteAsync(request, segments, cancellationToken).ConfigureAwait(false);

        await Http.WriteResponseAsync(stream, response, keepAlive, cancellationToken).ConfigureAwait(false);
        return keepAlive;
    }

    private static async Task<Response> RouteAsync(Request request, string[] segments, CancellationToken cancellationToken)
    {
        switch (segments)
        {
            case [] or ["json"]:
                return Response.Ok().Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteStartArray("items");
                    json.WriteNumberValue(1);
                    json.WriteNumberValue(2);
                    json.WriteNumberValue(3);
                    json.WriteEndArray();
                    json.WriteStartObject("nested");
                    json.WriteString("message", "hello");
                    json.WriteEndObject();
                    json.WriteBoolean("ok", true);
                    json.WriteEndObject();
                });

            case ["echo"]:
                return Response.Ok().Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteString("body", request.BodyText());
                    WriteOptional(json, "contentType", request.Header("content-type"));
                    json.WriteString("method", request.Method);
                    json.WriteString("path", request.Path);
                    json.WriteStartObject("query");
                    foreach (var (name, value) in request.QueryPairs().OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        json.WriteString(name, value);
                    }
                    json.WriteEndObject();
                    // The query as it arrived. `query` is a map, so it cannot show
                    // a parameter that was sent twice — which is a thing worth testing.
                    json.WriteString("rawQuery", request.Query);
                    json.WriteEndObject();
                });

            case ["headers"]:
                return Response.Ok().Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteStartArray("headers");
                    foreach (var (name, value) in request.Headers)
                    {
                        json.WriteStartArray();
                        json.WriteStringValue(name);
                        json.WriteStringValue(value);
                        json.WriteEndArray();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                });

            case ["status", var text]:
                {
                    var code = (ushort)Parse(text, 200);
                    return new Response(code, Reason(code)).Text($"status {code}");
                }

            case ["delay", var text]:
                {
                    var ms = Parse(text, 0);
                    await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
                    return Response.Ok().Json(json =>
                    {
                        json.WriteStartObject();
                        json.WriteNumber("delayedMs", ms);
                        json.WriteEndObject();
                    });
                }

            case ["binary"]:
                return Response.Ok()
                    .Header("content-type", "application/octet-stream")
                    .WithBody([0, 159, 146, 150, 255, 1, 2, 3]);

            case ["invalid-json"]:
                return Response.Ok()
                    .Header("content-type", "application/json")
                    .WithBody("{ this is not json"u8.ToArray());

            case ["redirect", var text]:
                {
                    var count = Parse(text, 0);
                    var target = count <= 1 ? "/json" : $"/redirect/{count - 1}";
                    return new Response(302, "Found").Header("location", target).Text("redirecting");
                }

            case ["redirect-loop"]:
                return new Response(302, "Found").Header("location", "/redirect-loop").Text("looping");

            case ["set-cookie"]:
                return Response.Ok().Header("set-cookie", "session=abc123; Path=/").Text("cookie set");

            case ["cookie"]:
                return Response.Ok().Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteString("cookie", request.Header("cookie") ?? "");
                    json.WriteEndObject();
                });

            case ["multipart"]:
                return Response.Ok().Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteString("body", request.BodyText());
                    WriteOptional(json, "contentType", request.Header("content-type"));
                    json.WriteEndObject();
                });

            case ["large", var text]:
                {
                    var body = new byte[Parse(text, 1024)];
                    Array.Fill(body, (byte)'x');
                    return Response.Ok().Header("content-type", "application/octet-stream").WithBody(body);
                }

            // An OAuth 2 token endpoint: issues a token for the right client, and
            // an RFC 6749 error object for the wrong one.
            case ["oauth", "token"]:
                return OAuthTokenRoute(request);

            // Anything with `Authorization: Bearer issued-token-N` gets through.
            case ["oauth", "protected"]:
                if (request.Header("authorization") is { } value
                    && value.StartsWith("Bearer issued-token-", StringComparison.Ordinal))
                {
                    var token = value;
                    while (token.StartsWith("Bearer ", StringComparison.Ordinal))
                    {
                        token = token["Bearer ".Length..];
                    }
                    return Response.Ok().Json(json =>
                    {
                        json.WriteStartObject();
                        json.WriteString("token", token);
                        json.WriteEndObject();
                    });
                }
                return new Response(401, "Unauthorized").Json(json => Error(json, "unauthorized"));

            // Digest, properly: challenge, then verify the client's arithmetic.
            case ["digest"]:
                return DigestRoute(request);

            case ["auth"]:
                return Response.Ok().Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteString("authorization", request.Header("authorization") ?? "");
                    json.WriteEndObject();
                });

            default:
                return new Response(404, "Not Found").Json(json => Error(json, "not found"));
        }
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private static void Error(Utf8JsonWriter json, string error, string? description = null)
    {
        json.WriteStartObject();
        json.WriteString("error", error);
        if (description is not null)
        {
            json.WriteString("error_description", description);
        }
        json.WriteEndObject();
    }

    // The account this server knows.
    private const string NtlmUser = "ada";
    private const string NtlmPassword = "lovelace";
    private const string NtlmDomain = "LOVELACE-LTD";

    /// <summary>
    /// An NTLM server, including the part that matters: the challenge lives on
    /// the connection. A client that sends its authenticate message on a
    /// different socket finds no challenge waiting and is refused, which is how
    /// a real server behaves and the only way to test that the handshake held
    /// one connection.
    /// </summary>
    private static Response NtlmRoute(Request request, ConnectionState state)
    {
        if (request.Header("authorization") is not { } header)
        {
            return NtlmChallengeResponse(null);
        }
        var trimmed = header.Trim();
        if (!trimmed.StartsWith("NTLM ", StringComparison.Ordinal))
        {
            return NtlmChallengeResponse(null);
        }

        byte[] message;
        try
        {
            message = Convert.FromBase64String(trimmed["NTLM ".Length..].Trim());
        }
        catch (FormatException)
        {
            return new Response(400, "Bad Request").Json(json => Error(json, "not base64"));
        }
        if (message.Length < 12 || !message.AsSpan(0, 8).SequenceEqual("NTLMSSP\0"u8))
        {
            return new Response(400, "Bad Request").Json(json => Error(json, "not ntlmssp"));
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8));
        switch (type)
        {
            // Negotiate: answer with a challenge and remember it for this socket.
            case 1:
                byte[] challenge = [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef];
                state.NtlmChallenge = challenge;
                return NtlmChallengeResponse(challenge);

            // Authenticate: only answerable against this connection's challenge.
            case 3:
                return state.NtlmChallenge is { } held
                    ? VerifyNtlmAuthenticate(message, held, state)
                    : new Response(401, "Unauthorized").Json(json => Error(json, "no challenge on this connection"));

            default:
                return new Response(400, "Bad Request").Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteNumber("messageType", type);
                    json.WriteEndObject();
                });
        }
    }

    private static Response NtlmChallengeResponse(byte[]? challenge)
    {
        string value;
        if (challenge is null)
        {
            value = "NTLM";
        }
        else
        {
            var message = new List<byte>();
            message.AddRange("NTLMSSP\0"u8.ToArray());
            AddU32(message, 2);
            // Target name: empty, pointing past the fixed header.
            message.AddRange([0, 0, 0, 0]);
            AddU32(message, 48);
            AddU32(message, 0x0008_0201); // unicode, ntlm, ess
            message.AddRange(challenge);
            message.AddRange(new byte[8]); // reserved

            // Target info: one NetBIOS domain pair and a terminator.
            var info = new List<byte>();
            AddU16(info, 2);
            var name = Encoding.Unicode.GetBytes("TESTSERVER");
            AddU16(info, (ushort)name.Length);
            info.AddRange(name);
            info.AddRange([0, 0, 0, 0]);

            AddU16(message, (ushort)info.Count);
            AddU16(message, (ushort)info.Count);
            AddU32(message, 48);
            message.AddRange(info);

            value = "NTLM " + Convert.ToBase64String(message.ToArray());
        }

        return new Response(401, "Unauthorized")
            .Header("WWW-Authenticate", value)
            .Json(json => Error(json, "unauthorized"));
    }

    /// <summary>Recomputes the NTLMv2 proof from the blob the client sent.</summary>
    private static Response VerifyNtlmAuthenticate(byte[] message, byte[] challenge, ConnectionState state)
    {
        byte[]? Field(int at)
        {
            if (at + 8 > message.Length)
            {
                return null;
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(at));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(at + 4));
            return offset + length > (ulong)message.Length ? null : message.AsSpan((int)offset, length).ToArray();
        }

        // NT response at 20, domain at 28, user at 36.
        if (Field(20) is not { } ntResponse || Field(28) is not { } domainBytes || Field(36) is not { } userBytes)
        {
            return new Response(400, "Bad Request").Json(json => Error(json, "truncated message"));
        }
        if (ntResponse.Length < 16)
        {
            return new Response(400, "Bad Request").Json(json => Error(json, "no proof"));
        }

        var proof = ntResponse[..16];
        var blob = ntResponse[16..];
        var user = FromUtf16Le(userBytes);
        var domain = FromUtf16Le(domainBytes);

        if (user != NtlmUser || domain != NtlmDomain)
        {
            return new Response(401, "Unauthorized").Json(json => Error(json, "unknown account"));
        }

        // NTOWFv2, then the proof over the server challenge and the blob.
        var ntHash = Md4(Encoding.Unicode.GetBytes(NtlmPassword));
        var identity = Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain);
        var key = HMACMD5.HashData(ntHash, identity);

        if (!HMACMD5.HashData(key, (byte[])[.. challenge, .. blob]).AsSpan().SequenceEqual(proof))
        {
            return new Response(401, "Unauthorized").Json(json => Error(json, "bad proof"));
        }

        // Authenticated for the life of this connection, as NTLM has it.
        state.NtlmChallenge = null;
        var requests = state.Requests;
        return Response.Ok().Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("authenticated", true);
            json.WriteString("domain", domain);
            json.WriteNumber("requestsOnThisConnection", requests);
            json.WriteString("user", user);
            json.WriteEndObject();
        });
    }

    private static string FromUtf16Le(byte[] bytes) => Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);

    private static void AddU16(List<byte> target, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        target.AddRange(bytes);
    }

    private static void AddU32(List<byte> target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.AddRange(bytes);
    }

    /// <summary>Counts tokens issued, so a test can tell a fresh token from a cached one.</summary>
    private static int s_tokensIssued;

    // The client this endpoint knows, however it identifies itself.
    private const string OAuthClientId = "test-client";
    private const string OAuthClientSecret = "test-secret";

    private static Response OAuthTokenRoute(Request request)
    {
        var form = FormPairs(request.BodyText());
        string Get(string key) => form.TryGetValue(key, out var value) ? value : "";

        // Either the basic header or the body carries the client's identity.
        var (id, secret) = request.Header("authorization") is { } header && BasicCredentials(header) is { } pair
            ? pair
            : (Get("client_id"), Get("client_secret"));

        if (id != OAuthClientId || secret != OAuthClientSecret)
        {
            return new Response(401, "Unauthorized")
                .Json(json => Error(json, "invalid_client", "The client is not this client."));
        }

        var refused = Get("grant_type") switch
        {
            "client_credentials" => null,
            "password" => Get("username") != "ada" || Get("password") != "lovelace" ? "invalid_grant" : null,
            "refresh_token" => Get("refresh_token") != "a-refresh-token" ? "invalid_grant" : null,
            _ => "unsupported_grant_type",
        };

        if (refused is not null)
        {
            return new Response(400, "Bad Request").Json(json => Error(json, refused, "The grant was refused."));
        }

        var issued = Interlocked.Increment(ref s_tokensIssued) - 1;
        var scope = Get("scope");
        return Response.Ok().Json(json =>
        {
            json.WriteStartObject();
            json.WriteString("access_token", $"issued-token-{issued}");
            json.WriteNumber("expires_in", 3600);
            json.WriteString("scope", scope);
            json.WriteString("token_type", "Bearer");
            json.WriteEndObject();
        });
    }

    /// <summary><c>user=a&amp;pass=b</c> into a map, for a form-encoded body.</summary>
    private static Dictionary<string, string> FormPairs(string body)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (pair.Length == 0 || equals < 0)
            {
                continue;
            }
            pairs[Http.Decode(pair[..equals])] = Http.Decode(pair[(equals + 1)..]);
        }
        return pairs;
    }

    /// <summary>The username and password out of a <c>Basic</c> header.</summary>
    private static (string, string)? BasicCredentials(string header)
    {
        var trimmed = header.Trim();
        if (!trimmed.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return null;
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(trimmed["Basic ".Length..].Trim()));
        }
        catch (Exception err) when (err is FormatException or DecoderFallbackException)
        {
            return null;
        }
        var colon = text.IndexOf(':');
        return colon < 0 ? null : (text[..colon], text[(colon + 1)..]);
    }

    // The fixed nonce this server challenges with. A real one would be random
    // and time-limited; a constant makes the test's arithmetic checkable by hand.
    private const string DigestNonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093";
    private const string DigestRealm = "testserver";
    private const string DigestUser = "ada";
    private const string DigestPassword = "lovelace";

    /// <summary>
    /// 401 with a challenge until the client answers it correctly, then 200.
    ///
    /// The response is recomputed here rather than pattern-matched, so the test
    /// fails if the client's digest is merely well-formed.
    /// </summary>
    private static Response DigestRoute(Request request)
    {
        if (request.Header("authorization") is not { } header)
        {
            return DigestChallenge();
        }

        var parts = DigestParts(header);
        string Get(string key) => parts.TryGetValue(key, out var value) ? value : "";

        if (Get("nonce") != DigestNonce || Get("username") != DigestUser)
        {
            return DigestChallenge();
        }

        var ha1 = Md5Hex($"{DigestUser}:{DigestRealm}:{DigestPassword}");
        var ha2 = Md5Hex($"{request.Method}:{Get("uri")}");
        var expected = Get("qop").Length == 0
            ? Md5Hex($"{ha1}:{DigestNonce}:{ha2}")
            : Md5Hex($"{ha1}:{DigestNonce}:{Get("nc")}:{Get("cnonce")}:{Get("qop")}:{ha2}");

        if (Get("response") != expected)
        {
            return DigestChallenge();
        }

        var uri = Get("uri");
        return Response.Ok().Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("authenticated", true);
            json.WriteString("uri", uri);
            json.WriteEndObject();
        });
    }

    private static Response DigestChallenge() =>
        new Response(401, "Unauthorized")
            .Header(
                "WWW-Authenticate",
                $"Digest realm=\"{DigestRealm}\", qop=\"auth\", algorithm=MD5, nonce=\"{DigestNonce}\"")
            .Json(json => Error(json, "unauthorized"));

    /// <summary><c>Digest username="ada", realm="...", response="..."</c> into a map.</summary>
    private static Dictionary<string, string> DigestParts(string header)
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = header.Trim();
        var rest = trimmed.StartsWith("Digest ", StringComparison.Ordinal) ? trimmed["Digest ".Length..] : "";
        foreach (var part in rest.Split(','))
        {
            var piece = part.Trim();
            var equals = piece.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }
            parts[piece[..equals].Trim().ToLowerInvariant()] = piece[(equals + 1)..].Trim().Trim('"');
        }
        return parts;
    }

    private static string Md5Hex(string input) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>
    /// Sends the head immediately, then dribbles out chunks. Used to test
    /// cancellation and timeouts that land while the body is in flight.
    /// </summary>
    private static async Task SlowBodyAsync(Stream stream, int chunks, int delayMs, CancellationToken cancellationToken)
    {
        const string head = "HTTP/1.1 200 OK\r\n"
            + "content-type: text/plain\r\n"
            + "transfer-encoding: chunked\r\n"
            + "connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < chunks; index++)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            var payload = $"chunk-{index}\n";
            await stream.WriteAsync(
                    Encoding.ASCII.GetBytes($"{payload.Length:x}\r\n{payload}\r\n"),
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int Parse(string value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        418 => "I'm a teapot",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    /// <summary>MD4 (RFC 1320), which the NTLM hash is built on and the platform does not provide.</summary>
    private static byte[] Md4(byte[] input)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

        var padded = new byte[((input.Length + 8) / 64 + 1) * 64];
        input.CopyTo(padded, 0);
        padded[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), (ulong)input.Length * 8);

        var x = new uint[16];
        for (var block = 0; block < padded.Length; block += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + i * 4));
            }

            uint aa = a, bb = b, cc = c, dd = d;

            static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
            static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
            static uint H(uint x, uint y, uint z) => x ^ y ^ z;

            foreach (var i in (int[])[0, 4, 8, 12])
            {
                a = uint.RotateLeft(a + F(b, c, d) + x[i], 3);
                d = uint.RotateLeft(d + F(a, b, c) + x[i + 1], 7);
                c = uint.RotateLeft(c + F(d, a, b) + x[i + 2], 11);
                b = uint.RotateLeft(b + F(c, d, a) + x[i + 3], 19);
            }
            foreach (var i in (int[])[0, 1, 2, 3])
            {
                a = uint.RotateLeft(a + G(b, c, d) + x[i] + 0x5a827999, 3);
                d = uint.RotateLeft(d + G(a, b, c) + x[i + 4] + 0x5a827999, 5);
                c = uint.RotateLeft(c + G(d, a, b) + x[i + 8] + 0x5a827999, 9);
                b = uint.RotateLeft(b + G(c, d, a) + x[i + 12] + 0x5a827999, 13);
            }
            foreach (var i in (int[])[0, 2, 1, 3])
            {
                a = uint.RotateLeft(a + H(b, c, d) + x[i] + 0x6ed9eba1, 3);
                d = uint.RotateLeft(d + H(a, b, c) + x[i + 8] + 0x6ed9eba1, 9);
                c = uint.RotateLeft(c + H(d, a, b) + x[i + 4] + 0x6ed9eba1, 11);
                b = uint.RotateLeft(b + H(c, d, a) + x[i + 12] + 0x6ed9eba1, 15);
            }

            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d);
        return digest;
    }
}
