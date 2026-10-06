using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// HTTP Digest, which cannot be applied before the server has spoken.
///
/// Basic and bearer go out with the first request. Digest is a conversation:
/// the server answers 401 with a nonce, and only then can a response be
/// computed. So the engine sends once, reads the challenge, and sends again —
/// which is what a browser does, and why digest costs a round trip.
/// </summary>
internal static class Digest
{
    /// <summary>
    /// Builds the <c>Authorization</c> value answering a challenge.
    ///
    /// Null, with <paramref name="why"/> set, is a sentence for the user rather
    /// than a failure: a challenge we cannot answer leaves the 401 in front of
    /// them, which is the truth of what happened, with a note saying why no
    /// second attempt was made.
    /// </summary>
    public static string? Answer(
        HttpRequest request,
        HttpResponse response,
        string username,
        string password,
        out string? why)
    {
        why = null;
        var challenge = response.Header("www-authenticate");
        if (challenge is null)
        {
            why = "The server asked for auth but sent no challenge.";
            return null;
        }

        if (!AsciiLower(challenge.TrimStart()).StartsWith("digest", StringComparison.Ordinal))
        {
            why = $"The server asked for {SchemeName(challenge)} auth, not Digest.";
            return null;
        }

        DigestChallenge prompt;
        try
        {
            prompt = DigestChallenge.Parse(challenge);
        }
        catch (DigestException err)
        {
            why = $"The server's Digest challenge could not be read: {err.Message}.";
            return null;
        }

        // The digest is computed over the path and query actually requested,
        // not the whole URL.
        var uri = PathAndQuery(response.FinalUrl);

        try
        {
            return prompt.Respond(username, password, uri, request.Method.AsString(), RandomCnonce());
        }
        catch (DigestException err)
        {
            why = $"This Digest challenge could not be answered: {err.Message}.";
            return null;
        }
    }

    internal static string SchemeName(string challenge)
    {
        var first = challenge.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first ?? "unknown";
    }

    internal static string PathAndQuery(string url) =>
        // Only falls back if the URL we just requested will not parse back.
        WebUrl.TryParse(url, out var parsed, out _) ? parsed.PathAndQuery : "/";

    private static string RandomCnonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Lower-cases A–Z only, leaving every other character alone.</summary>
    internal static string AsciiLower(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var c = source[index];
                span[index] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    /// <summary>Upper-cases a–z only, leaving every other character alone.</summary>
    internal static string AsciiUpper(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var c = source[index];
                span[index] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            }
        });
}

/// <summary>A challenge that could not be read or answered; the message is shown to the user.</summary>
internal sealed class DigestException(string message) : Exception(message);

internal enum DigestHash
{
    Md5,
    Sha256,
    Sha512_256,
}

internal enum DigestQop
{
    Auth,
    AuthInt,
}

/// <summary>A parsed <c>WWW-Authenticate: Digest ...</c>, as RFC 7616 describes it.</summary>
internal sealed class DigestChallenge
{
    public List<string>? Domain { get; private init; }
    public string Realm { get; private init; } = "";
    public string Nonce { get; private init; } = "";
    public string? Opaque { get; private init; }
    public bool Stale { get; private init; }
    public string? Charset { get; private init; }
    public DigestHash Hash { get; private init; }
    public bool Session { get; private init; }
    public List<DigestQop>? Qop { get; private init; }
    public bool UserHash { get; private init; }

    /// <summary>How many times this challenge has been answered; each answer counts up.</summary>
    public uint NonceCount { get; private set; }

    public static DigestChallenge Parse(string header)
    {
        var input = header.Trim();
        if (input.StartsWith("Digest", StringComparison.Ordinal))
        {
            input = input["Digest".Length..];
        }

        var fields = ParseHeaderMap(input);

        List<string>? domain = null;
        if (fields.TryGetValue("domain", out var domains))
        {
            domain = domains.Split(' ').Select(d => d.Trim()).Where(d => d.Length > 0).ToList();
        }

        var realm = fields.TryGetValue("realm", out var r) ? r : throw Missing("realm", input);
        var nonce = fields.TryGetValue("nonce", out var n) ? n : throw Missing("nonce", input);
        fields.TryGetValue("opaque", out var opaque);
        var stale = fields.TryGetValue("stale", out var s) && Digest.AsciiLower(s) == "true";

        string? charset = null;
        if (fields.TryGetValue("charset", out var c))
        {
            charset = c is "ASCII" or "UTF-8" ? c : throw new DigestException($"Bad charset: {c}");
        }

        var (hash, session) = (DigestHash.Md5, false);
        if (fields.TryGetValue("algorithm", out var algorithm))
        {
            (hash, session) = algorithm switch
            {
                "MD5" => (DigestHash.Md5, false),
                "MD5-sess" => (DigestHash.Md5, true),
                "SHA-256" => (DigestHash.Sha256, false),
                "SHA-256-sess" => (DigestHash.Sha256, true),
                "SHA-512-256" => (DigestHash.Sha512_256, false),
                "SHA-512-256-sess" => (DigestHash.Sha512_256, true),
                _ => throw new DigestException($"Unknown algorithm: {algorithm}"),
            };
        }

        List<DigestQop>? qop = null;
        if (fields.TryGetValue("qop", out var offered))
        {
            qop = [];
            foreach (var option in offered.Split(','))
            {
                qop.Add(option.Trim() switch
                {
                    "auth" => DigestQop.Auth,
                    "auth-int" => DigestQop.AuthInt,
                    var other => throw new DigestException($"Bad Qop option: {other}"),
                });
            }
        }

        var userHash = fields.TryGetValue("userhash", out var u) && Digest.AsciiLower(u) == "true";

        return new DigestChallenge
        {
            Domain = domain,
            Realm = realm,
            Nonce = nonce,
            Opaque = opaque,
            Stale = stale,
            Charset = charset,
            Hash = hash,
            Session = session,
            Qop = qop,
            UserHash = userHash,
        };
    }

    private static DigestException Missing(string what, string input) =>
        new($"Missing \"{what}\" in header: {input}");

    private enum State
    {
        White,
        Name,
        ValueBegin,
        ValueQuoted,
        ValueQuotedNextLiteral,
        ValuePlain,
    }

    /// <summary><c>name=value</c> and <c>name="quoted value"</c> pairs; a repeated name keeps its last value.</summary>
    internal static Dictionary<string, string> ParseHeaderMap(string input)
    {
        var state = State.White;
        var nameStart = 0;
        string? token = null;
        var value = new StringBuilder();
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < input.Length; index++)
        {
            var c = input[index];
            switch (state)
            {
                case State.White:
                    if (char.IsLetter(c))
                    {
                        nameStart = index;
                        state = State.Name;
                    }
                    break;

                case State.Name:
                    if (c == '=')
                    {
                        token = input[nameStart..index];
                        state = State.ValueBegin;
                    }
                    break;

                case State.ValueBegin:
                    value.Clear();
                    if (c == '"')
                    {
                        state = State.ValueQuoted;
                    }
                    else
                    {
                        value.Append(c);
                        state = State.ValuePlain;
                    }
                    break;

                case State.ValueQuoted:
                    if (c == '"')
                    {
                        parsed[token!] = value.ToString();
                        token = null;
                        value.Clear();
                        state = State.White;
                    }
                    else if (c == '\\')
                    {
                        state = State.ValueQuotedNextLiteral;
                    }
                    else
                    {
                        value.Append(c);
                    }
                    break;

                case State.ValueQuotedNextLiteral:
                    value.Append(c);
                    state = State.ValueQuoted;
                    break;

                case State.ValuePlain:
                    if (c == ',' || IsAsciiWhitespace(c))
                    {
                        parsed[token!] = value.ToString();
                        token = null;
                        value.Clear();
                        state = State.White;
                    }
                    else
                    {
                        value.Append(c);
                    }
                    break;
            }
        }

        switch (state)
        {
            case State.ValuePlain:
                parsed[token!] = value.ToString();
                break;
            case State.White:
                break;
            default:
                throw new DigestException($"Invalid header syntax: {input}");
        }

        return parsed;
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    /// <summary>
    /// The <c>Authorization</c> value for one answer. <paramref name="body"/>
    /// is only needed for <c>auth-int</c>; without one, plain <c>auth</c> is
    /// used instead.
    /// </summary>
    public string Respond(string username, string password, string uri, string method, string cnonce, byte[]? body = null)
    {
        DigestQop? qop = null;
        if (Qop is { } offered)
        {
            if (offered.Contains(DigestQop.AuthInt))
            {
                qop = DigestQop.AuthInt;
            }
            else if (offered.Contains(DigestQop.Auth))
            {
                qop = DigestQop.Auth;
            }
            else
            {
                throw new DigestException($"Illegal Qop in prompt: {string.Join(", ", offered.Select(QopName))}");
            }
        }

        NonceCount++;

        // auth-int needs a body to hash; without one the answer is plain auth.
        if (qop == DigestQop.AuthInt && body is null)
        {
            qop = DigestQop.Auth;
        }

        string? usedCnonce = qop is null ? null : cnonce;

        var a1 = $"{username}:{Realm}:{password}";
        if (Session)
        {
            a1 = $"{H(a1)}:{Nonce}:{usedCnonce ?? ""}";
        }

        var a2 = qop == DigestQop.AuthInt
            ? $"{method}:{uri}:{H(body!)}"
            : $"{method}:{uri}";

        var sentUsername = UserHash ? H($"{username}:{Realm}") : username;
        var nc = NonceCount.ToString("x8", CultureInfo.InvariantCulture);

        var response = qop is { } q
            ? H($"{H(a1)}:{Nonce}:{nc}:{usedCnonce}:{QopName(q)}:{H(a2)}")
            : H($"{H(a1)}:{Nonce}:{H(a2)}");

        var entries = new List<string>
        {
            Quoted("username", sentUsername),
            Quoted("realm", Realm),
            Quoted("nonce", Nonce),
            Quoted("uri", uri),
        };
        if (qop is { } chosen && usedCnonce is not null)
        {
            entries.Add($"qop={QopName(chosen)}");
            entries.Add($"nc={nc}");
            entries.Add(Quoted("cnonce", usedCnonce));
        }
        entries.Add(Quoted("response", response));
        if (Opaque is not null)
        {
            entries.Add(Quoted("opaque", Opaque));
        }
        // Plain MD5 without qop is the RFC 2069 form, which never named it.
        if (qop is not null || Hash != DigestHash.Md5 || Session)
        {
            entries.Add($"algorithm={AlgorithmName()}");
        }
        if (UserHash)
        {
            entries.Add("userhash=true");
        }

        return "Digest " + string.Join(", ", entries);
    }

    private static string Quoted(string name, string value) =>
        $"{name}=\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string QopName(DigestQop qop) => qop == DigestQop.Auth ? "auth" : "auth-int";

    private string AlgorithmName()
    {
        var name = Hash switch
        {
            DigestHash.Md5 => "MD5",
            DigestHash.Sha256 => "SHA-256",
            _ => "SHA-512-256",
        };
        return Session ? name + "-sess" : name;
    }

    private string H(string value) => H(Encoding.UTF8.GetBytes(value));

    private string H(byte[] value) => Convert.ToHexStringLower(Hash switch
    {
        DigestHash.Md5 => MD5.HashData(value),
        DigestHash.Sha256 => SHA256.HashData(value),
        _ => Sha512.Hash512_256(value),
    });
}

/// <summary>
/// SHA-512 with a choice of starting values. The platform has SHA-512 itself
/// but not the SHA-512/256 variant (FIPS 180-4 §5.3.6), which Digest names.
/// </summary>
internal static class Sha512
{
    private static readonly ulong[] K =
    [
        0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc,
        0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
        0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2,
        0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
        0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65,
        0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
        0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4,
        0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
        0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df,
        0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
        0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30,
        0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
        0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8,
        0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
        0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec,
        0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
        0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178,
        0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
        0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c,
        0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
    ];

    /// <summary>The standard SHA-512 starting values.</summary>
    internal static readonly ulong[] Sha512Iv =
    [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];

    /// <summary>SHA-512/256's own starting values.</summary>
    private static readonly ulong[] Sha512_256Iv =
    [
        0x22312194fc2bf72c, 0x9f555fa3c84c64c2, 0x2393b86b6f53b151, 0x963877195940eabd,
        0x96283ee2a88effe3, 0xbe5e1e2553863992, 0x2b0199fc2c85b8aa, 0x0eb72ddc81c52ca2,
    ];

    public static byte[] Hash512_256(byte[] data) => Compute(data, Sha512_256Iv)[..32];

    /// <summary>The full 64-byte state after hashing <paramref name="data"/> from <paramref name="iv"/>.</summary>
    internal static byte[] Compute(byte[] data, ulong[] iv)
    {
        var h = (ulong[])iv.Clone();

        // Padding: a one bit, zeros, then the length in bits as 128 bits.
        var paddedLength = (data.Length + 17 + 127) / 128 * 128;
        var message = new byte[paddedLength];
        data.CopyTo(message, 0);
        message[data.Length] = 0x80;
        var bits = (ulong)data.Length * 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(paddedLength - 8), bits);

        var w = new ulong[80];
        for (var block = 0; block < paddedLength; block += 128)
        {
            for (var t = 0; t < 16; t++)
            {
                w[t] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(message.AsSpan(block + t * 8));
            }
            for (var t = 16; t < 80; t++)
            {
                var s0 = ulong.RotateRight(w[t - 15], 1) ^ ulong.RotateRight(w[t - 15], 8) ^ (w[t - 15] >> 7);
                var s1 = ulong.RotateRight(w[t - 2], 19) ^ ulong.RotateRight(w[t - 2], 61) ^ (w[t - 2] >> 6);
                w[t] = w[t - 16] + s0 + w[t - 7] + s1;
            }

            ulong a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (var t = 0; t < 80; t++)
            {
                var sum1 = ulong.RotateRight(e, 14) ^ ulong.RotateRight(e, 18) ^ ulong.RotateRight(e, 41);
                var choose = (e & f) ^ (~e & g);
                var temp1 = hh + sum1 + choose + K[t] + w[t];
                var sum0 = ulong.RotateRight(a, 28) ^ ulong.RotateRight(a, 34) ^ ulong.RotateRight(a, 39);
                var majority = (a & b) ^ (a & c) ^ (b & c);
                var temp2 = sum0 + majority;

                hh = g;
                g = f;
                f = e;
                e = d + temp1;
                d = c;
                c = b;
                b = a;
                a = temp1 + temp2;
            }

            h[0] += a;
            h[1] += b;
            h[2] += c;
            h[3] += d;
            h[4] += e;
            h[5] += f;
            h[6] += g;
            h[7] += hh;
        }

        var output = new byte[64];
        for (var index = 0; index < 8; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(index * 8), h[index]);
        }
        return output;
    }
}
