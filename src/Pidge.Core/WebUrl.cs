using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Pidge.Core.Idna;

namespace Pidge.Core;

/// <summary>
/// A WHATWG URL, parsed and serialized as the URL Standard says, so that every
/// URL the app shows, sends or writes into generated code is spelt the way a
/// browser would spell it. <see cref="Uri"/> normalizes differently
/// (it unescapes, it keeps default ports, it treats a missing path its own way),
/// which is why it is not used for anything the user can see.
///
/// Only the special schemes are supported — http, https, ws, wss and ftp —
/// which is everything a request can be sent to.
/// </summary>
public sealed class WebUrl
{
    private WebUrl(string scheme, string host)
    {
        Scheme = scheme;
        Host = host;
    }

    /// <summary>Lower case, without the colon.</summary>
    public string Scheme { get; }

    /// <summary>Percent-encoded, as it appears in the URL. Empty when absent.</summary>
    public string Username { get; private set; } = "";

    /// <summary>Percent-encoded. Null when absent; an empty password is absent.</summary>
    public string? Password { get; private set; }

    /// <summary>
    /// The serialized host: a lower-case ASCII domain, a dotted IPv4 address,
    /// or an IPv6 address in brackets.
    /// </summary>
    public string Host { get; }

    /// <summary>The explicit port, or null when absent or the scheme's default.</summary>
    public int? Port { get; private set; }

    /// <summary>Each segment percent-encoded. Never empty: <c>/</c> is one empty segment.</summary>
    public IReadOnlyList<string> PathSegments => _path;

    private List<string> _path = [];

    /// <summary>Percent-encoded, without the <c>?</c>. Null when there is no query.</summary>
    public string? Query { get; private set; }

    /// <summary>Percent-encoded, without the <c>#</c>. Null when there is no fragment.</summary>
    public string? Fragment { get; private set; }

    /// <summary>The path, starting with <c>/</c>.</summary>
    public string Path => "/" + string.Join("/", _path);

    /// <summary>The host without IPv6 brackets, for a socket.</summary>
    public string HostForConnect => Host.StartsWith('[') ? Host[1..^1] : Host;

    public bool IsIpv6Host => Host.StartsWith('[');

    public int PortOrKnownDefault => Port ?? DefaultPort(Scheme) ?? 0;

    /// <summary>Path and query, as an HTTP request line carries them.</summary>
    public string PathAndQuery => Query is null ? Path : Path + "?" + Query;

    public static int? DefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        "ftp" => 21,
        _ => null,
    };

    public override string ToString()
    {
        var output = new StringBuilder(Scheme.Length + Host.Length + 32);
        output.Append(Scheme).Append("://");
        if (Username.Length > 0 || !string.IsNullOrEmpty(Password))
        {
            output.Append(Username);
            if (!string.IsNullOrEmpty(Password))
            {
                output.Append(':').Append(Password);
            }
            output.Append('@');
        }
        output.Append(Host);
        if (Port is { } port)
        {
            output.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        output.Append(Path);
        if (Query is not null)
        {
            output.Append('?').Append(Query);
        }
        if (Fragment is not null)
        {
            output.Append('#').Append(Fragment);
        }
        return output.ToString();
    }

    public WebUrl Clone() => new(Scheme, Host)
    {
        Username = Username,
        Password = Password,
        Port = Port,
        _path = [.. _path],
        Query = Query,
        Fragment = Fragment,
    };

    /// <summary>
    /// Replaces the query: tabs and newlines are
    /// dropped and anything that cannot appear in a query is escaped. What is
    /// already escaped stays as it is.
    /// </summary>
    public void SetQuery(string? query) =>
        Query = query is null ? null : PercentEncoding.Encode(StripTabsAndNewlines(query), EncodeSet.SpecialQuery);

    public void SetFragment(string? fragment) =>
        Fragment = fragment is null ? null : PercentEncoding.Encode(StripTabsAndNewlines(fragment), EncodeSet.Fragment);

    /// <summary>The query's decoded name/value pairs, read as a form.</summary>
    public List<(string Name, string Value)> QueryPairs() =>
        Query is null ? [] : PercentEncoding.FormParse(Query);

    /// <summary>Parses, or throws <see cref="FormatException"/> with the parse error's message.</summary>
    public static WebUrl Parse(string input) =>
        TryParse(input, out var url, out var error) ? url : throw new FormatException(error);

    /// <summary>
    /// Parses with the WHATWG basic URL parser. <paramref name="error"/> is a short
    /// description of what failed, e.g. <c>invalid port number</c>.
    /// </summary>
    public static bool TryParse(string input, [NotNullWhen(true)] out WebUrl? url, out string error)
    {
        url = null;
        error = "";

        var start = 0;
        var end = input.Length;
        while (start < end && input[start] <= ' ')
        {
            start++;
        }
        while (end > start && input[end - 1] <= ' ')
        {
            end--;
        }
        var text = StripTabsAndNewlines(input[start..end]);

        // Scheme.
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]))
        {
            error = "relative URL without a base";
            return false;
        }
        var p = 1;
        while (p < text.Length && (char.IsAsciiLetterOrDigit(text[p]) || text[p] is '+' or '-' or '.'))
        {
            p++;
        }
        if (p >= text.Length || text[p] != ':')
        {
            error = "relative URL without a base";
            return false;
        }
        var scheme = text[..p].ToLowerInvariant();
        if (DefaultPort(scheme) is not { } defaultPort)
        {
            error = $"unsupported scheme `{scheme}`";
            return false;
        }
        p++;

        // Special authority (ignore) slashes: any run of / and \.
        while (p < text.Length && text[p] is '/' or '\\')
        {
            p++;
        }

        // Authority, up to the path, query or fragment.
        var authorityEnd = p;
        while (authorityEnd < text.Length && text[authorityEnd] is not ('/' or '\\' or '?' or '#'))
        {
            authorityEnd++;
        }
        var authority = text[p..authorityEnd];

        var username = "";
        string? password = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = authority[..at];
            var colon = userinfo.IndexOf(':');
            if (colon >= 0)
            {
                username = PercentEncoding.Encode(userinfo[..colon], EncodeSet.Userinfo);
                var encoded = PercentEncoding.Encode(userinfo[(colon + 1)..], EncodeSet.Userinfo);
                password = encoded.Length > 0 ? encoded : null;
            }
            else
            {
                username = PercentEncoding.Encode(userinfo, EncodeSet.Userinfo);
            }
            authority = authority[(at + 1)..];
        }

        // Host and port: the first colon outside brackets starts the port.
        var portStart = -1;
        var inBrackets = false;
        for (var i = 0; i < authority.Length; i++)
        {
            if (authority[i] == '[')
            {
                inBrackets = true;
            }
            else if (authority[i] == ']')
            {
                inBrackets = false;
            }
            else if (authority[i] == ':' && !inBrackets)
            {
                portStart = i;
                break;
            }
        }
        var hostText = portStart >= 0 ? authority[..portStart] : authority;
        if (hostText.Length == 0)
        {
            error = "empty host";
            return false;
        }
        if (!TryParseHost(hostText, out var host, out error))
        {
            return false;
        }

        int? port = null;
        if (portStart >= 0)
        {
            var digits = authority[(portStart + 1)..];
            if (digits.Any(c => !char.IsAsciiDigit(c)))
            {
                error = "invalid port number";
                return false;
            }
            if (digits.Length > 0)
            {
                var trimmed = digits.TrimStart('0');
                if (trimmed.Length > 5 || (trimmed.Length > 0 && int.Parse(trimmed, CultureInfo.InvariantCulture) > 65535))
                {
                    error = "invalid port number";
                    return false;
                }
                var value = trimmed.Length == 0 ? 0 : int.Parse(trimmed, CultureInfo.InvariantCulture);
                port = value == defaultPort ? null : value;
            }
        }

        // Path.
        var pathEnd = authorityEnd;
        while (pathEnd < text.Length && text[pathEnd] is not ('?' or '#'))
        {
            pathEnd++;
        }
        var path = ParsePath(text[authorityEnd..pathEnd]);

        // Query and fragment.
        string? query = null;
        string? fragment = null;
        var rest = text[pathEnd..];
        if (rest.StartsWith('?'))
        {
            var hash = rest.IndexOf('#');
            var raw = hash >= 0 ? rest[1..hash] : rest[1..];
            query = PercentEncoding.Encode(raw, EncodeSet.SpecialQuery);
            rest = hash >= 0 ? rest[hash..] : "";
        }
        if (rest.StartsWith('#'))
        {
            fragment = PercentEncoding.Encode(rest[1..], EncodeSet.Fragment);
        }

        url = new WebUrl(scheme, host)
        {
            Username = username,
            Password = password,
            Port = port,
            _path = path,
            Query = query,
            Fragment = fragment,
        };
        return true;
    }

    private static List<string> ParsePath(string input)
    {
        var segments = new List<string>();
        // The path start state swallows one leading slash.
        var body = input.Length > 0 && input[0] is '/' or '\\' ? input[1..] : input;
        var pieces = body.Split('/', '\\');
        for (var i = 0; i < pieces.Length; i++)
        {
            var last = i == pieces.Length - 1;
            var buffer = PercentEncoding.Encode(pieces[i], EncodeSet.Path);
            if (IsDoubleDot(buffer))
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                if (last)
                {
                    segments.Add("");
                }
            }
            else if (IsSingleDot(buffer))
            {
                if (last)
                {
                    segments.Add("");
                }
            }
            else
            {
                segments.Add(buffer);
            }
        }
        return segments;
    }

    private static bool IsSingleDot(string segment) =>
        segment == "." || segment.Equals("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleDot(string segment) =>
        segment.ToLowerInvariant() is ".." or ".%2e" or "%2e." or "%2e%2e";

    private static string StripTabsAndNewlines(string input) =>
        input.IndexOfAny(['\t', '\n', '\r']) < 0
            ? input
            : string.Concat(input.Where(c => c is not ('\t' or '\n' or '\r')));

    // ---- Host parsing -------------------------------------------------------

    private static bool TryParseHost(string input, out string host, out string error)
    {
        host = "";
        error = "";
        if (input.StartsWith('['))
        {
            if (!input.EndsWith(']') || !TryParseIpv6(input[1..^1], out var pieces))
            {
                error = "invalid IPv6 address";
                return false;
            }
            host = "[" + SerializeIpv6(pieces) + "]";
            return true;
        }

        var domain = PercentEncoding.Decode(input);
        // Forbidden code points (spaces, `|`, `<`, controls...) are refused here
        // too, so they report the same error as any other bad domain.
        if (!Uts46.TryToAscii(domain, out var ascii))
        {
            error = "invalid international domain name";
            return false;
        }
        if (ascii.Length == 0)
        {
            error = "empty host";
            return false;
        }
        if (EndsInANumber(ascii))
        {
            if (!TryParseIpv4(ascii, out var address))
            {
                error = "invalid IPv4 address";
                return false;
            }
            host = $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";
            return true;
        }
        host = ascii;
        return true;
    }


    private static bool EndsInANumber(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0)
        {
            if (parts.Count == 1)
            {
                return false;
            }
            parts.RemoveAt(parts.Count - 1);
        }
        var last = parts[^1];
        if (last.Length > 0 && last.All(char.IsAsciiDigit))
        {
            return true;
        }
        return TryParseIpv4Number(last, out _);
    }

    private static bool TryParseIpv4Number(string input, out ulong value)
    {
        value = 0;
        if (input.Length == 0)
        {
            return false;
        }
        var radix = 10;
        if (input.Length >= 2 && (input.StartsWith("0x", StringComparison.Ordinal) || input.StartsWith("0X", StringComparison.Ordinal)))
        {
            radix = 16;
            input = input[2..];
        }
        else if (input.Length >= 2 && input[0] == '0')
        {
            radix = 8;
            input = input[1..];
        }
        if (input.Length == 0)
        {
            return true;
        }
        foreach (var c in input)
        {
            int digit;
            if (char.IsAsciiDigit(c))
            {
                digit = c - '0';
            }
            else if (char.IsAsciiHexDigit(c))
            {
                digit = char.ToLowerInvariant(c) - 'a' + 10;
            }
            else
            {
                return false;
            }
            if (digit >= radix)
            {
                return false;
            }
            // Anything this large is out of range for every position anyway.
            value = Math.Min(value * (ulong)radix + (ulong)digit, 1UL << 40);
        }
        return true;
    }

    private static bool TryParseIpv4(string input, out uint address)
    {
        address = 0;
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0 && parts.Count > 1)
        {
            parts.RemoveAt(parts.Count - 1);
        }
        if (parts.Count > 4)
        {
            return false;
        }
        var numbers = new List<ulong>();
        foreach (var part in parts)
        {
            if (!TryParseIpv4Number(part, out var n))
            {
                return false;
            }
            numbers.Add(n);
        }
        for (var i = 0; i < numbers.Count - 1; i++)
        {
            if (numbers[i] > 255)
            {
                return false;
            }
        }
        if (numbers[^1] >= (ulong)Math.Pow(256, 5 - numbers.Count))
        {
            return false;
        }
        var ipv4 = numbers[^1];
        for (var i = 0; i < numbers.Count - 1; i++)
        {
            ipv4 += numbers[i] << (8 * (3 - i));
        }
        address = (uint)ipv4;
        return true;
    }

    private static bool TryParseIpv6(string input, out ushort[] address)
    {
        address = new ushort[8];
        var pieceIndex = 0;
        int? compress = null;
        var p = 0;
        char At(int i) => i < input.Length ? input[i] : '\0';
        bool Eof(int i) => i >= input.Length;

        if (At(p) == ':')
        {
            if (At(p + 1) != ':')
            {
                return false;
            }
            p += 2;
            pieceIndex++;
            compress = pieceIndex;
        }

        while (!Eof(p))
        {
            if (pieceIndex == 8)
            {
                return false;
            }
            if (At(p) == ':')
            {
                if (compress is not null)
                {
                    return false;
                }
                p++;
                pieceIndex++;
                compress = pieceIndex;
                continue;
            }

            var value = 0;
            var length = 0;
            while (length < 4 && !Eof(p) && char.IsAsciiHexDigit(At(p)))
            {
                value = value * 16 + Convert.ToInt32(At(p).ToString(), 16);
                p++;
                length++;
            }

            if (At(p) == '.' && !Eof(p))
            {
                if (length == 0)
                {
                    return false;
                }
                p -= length;
                if (pieceIndex > 6)
                {
                    return false;
                }
                var numbersSeen = 0;
                while (!Eof(p))
                {
                    int? ipv4Piece = null;
                    if (numbersSeen > 0)
                    {
                        if (At(p) == '.' && numbersSeen < 4)
                        {
                            p++;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    if (Eof(p) || !char.IsAsciiDigit(At(p)))
                    {
                        return false;
                    }
                    while (!Eof(p) && char.IsAsciiDigit(At(p)))
                    {
                        var number = At(p) - '0';
                        if (ipv4Piece is null)
                        {
                            ipv4Piece = number;
                        }
                        else if (ipv4Piece == 0)
                        {
                            return false;
                        }
                        else
                        {
                            ipv4Piece = ipv4Piece * 10 + number;
                        }
                        if (ipv4Piece > 255)
                        {
                            return false;
                        }
                        p++;
                    }
                    address[pieceIndex] = (ushort)(address[pieceIndex] * 0x100 + ipv4Piece!.Value);
                    numbersSeen++;
                    if (numbersSeen is 2 or 4)
                    {
                        pieceIndex++;
                    }
                }
                if (numbersSeen != 4)
                {
                    return false;
                }
                break;
            }
            else if (At(p) == ':' && !Eof(p))
            {
                p++;
                if (Eof(p))
                {
                    return false;
                }
            }
            else if (!Eof(p))
            {
                return false;
            }
            address[pieceIndex] = (ushort)value;
            pieceIndex++;
        }

        if (compress is { } c)
        {
            var swaps = pieceIndex - c;
            pieceIndex = 7;
            while (pieceIndex != 0 && swaps > 0)
            {
                (address[pieceIndex], address[c + swaps - 1]) = (address[c + swaps - 1], address[pieceIndex]);
                pieceIndex--;
                swaps--;
            }
        }
        else if (pieceIndex != 8)
        {
            return false;
        }
        return true;
    }

    private static string SerializeIpv6(ushort[] address)
    {
        // The first longest run of two or more zero pieces is compressed.
        int? compress = null;
        var bestLength = 1;
        for (var i = 0; i < 8;)
        {
            if (address[i] != 0)
            {
                i++;
                continue;
            }
            var runStart = i;
            while (i < 8 && address[i] == 0)
            {
                i++;
            }
            if (i - runStart > bestLength)
            {
                bestLength = i - runStart;
                compress = runStart;
            }
        }

        var output = new StringBuilder();
        var ignoreZero = false;
        for (var i = 0; i < 8; i++)
        {
            if (ignoreZero && address[i] == 0)
            {
                continue;
            }
            ignoreZero = false;
            if (compress == i)
            {
                output.Append(i == 0 ? "::" : ":");
                ignoreZero = true;
                continue;
            }
            output.Append(address[i].ToString("x", CultureInfo.InvariantCulture));
            if (i != 7)
            {
                output.Append(':');
            }
        }
        return output.ToString();
    }
}
