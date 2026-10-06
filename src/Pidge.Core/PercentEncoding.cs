using System.Text;

namespace Pidge.Core;

/// <summary>
/// The percent-encode sets of the WHATWG URL standard, so URLs come out spelt
/// the way a browser spells them.
/// </summary>
public enum EncodeSet
{
    /// <summary>C0 controls and DEL. Non-ASCII is always encoded.</summary>
    Controls,
    Fragment,
    Query,
    SpecialQuery,
    Path,
    Userinfo,
    /// <summary>Everything but RFC 3986's unreserved set: what <c>encodeURIComponent</c> and curl produce.</summary>
    Unreserved,
    /// <summary>
    /// Everything but ASCII alphanumerics: the <c>NON_ALPHANUMERIC</c> set.
    /// </summary>
    NonAlphanumeric,
}

public static class PercentEncoding
{
    private static readonly bool[][] Sets = BuildSets();

    private static bool[][] BuildSets()
    {
        var sets = new bool[Enum.GetValues<EncodeSet>().Length][];

        bool[] Make(Func<char, bool> encodes)
        {
            var table = new bool[128];
            for (var c = 0; c < 128; c++)
            {
                table[c] = c < 0x20 || c == 0x7F || encodes((char)c);
            }
            return table;
        }

        static bool In(char c, string chars) => chars.Contains(c);

        sets[(int)EncodeSet.Controls] = Make(_ => false);
        sets[(int)EncodeSet.Fragment] = Make(c => In(c, " \"<>`"));
        sets[(int)EncodeSet.Query] = Make(c => In(c, " \"#<>"));
        sets[(int)EncodeSet.SpecialQuery] = Make(c => In(c, " \"#<>'"));
        // url 2.5's PATH: FRAGMENT plus # ? { }.
        sets[(int)EncodeSet.Path] = Make(c => In(c, " \"<>`#?{}"));
        sets[(int)EncodeSet.Userinfo] = Make(c => In(c, " \"<>`#?{}/:;=@[\\]^|"));
        sets[(int)EncodeSet.Unreserved] = Make(c => !(char.IsAsciiLetterOrDigit(c) || In(c, "-._~")));
        sets[(int)EncodeSet.NonAlphanumeric] = Make(c => !char.IsAsciiLetterOrDigit(c));
        return sets;
    }

    /// <summary>Percent-encodes UTF-8 bytes outside <paramref name="set"/>'s allowed characters.</summary>
    public static string Encode(string input, EncodeSet set)
    {
        var table = Sets[(int)set];
        StringBuilder? output = null;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c < 128 && !table[c])
            {
                output?.Append(c);
                continue;
            }
            output ??= new StringBuilder(input.Length + 16).Append(input, 0, i);
            AppendEncoded(output, input, ref i);
        }
        return output?.ToString() ?? input;
    }

    private static void AppendEncoded(StringBuilder output, string input, ref int i)
    {
        Span<byte> bytes = stackalloc byte[4];
        int count;
        var c = input[i];
        if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
        {
            count = Encoding.UTF8.GetBytes(input.AsSpan(i, 2), bytes);
            i++;
        }
        else if (char.IsSurrogate(c))
        {
            // A lone surrogate is not text, and has no UTF-8 to encode.
            count = Encoding.UTF8.GetBytes("�", bytes);
        }
        else
        {
            count = Encoding.UTF8.GetBytes(input.AsSpan(i, 1), bytes);
        }
        for (var b = 0; b < count; b++)
        {
            AppendByte(output, bytes[b]);
        }
    }

    private static void AppendByte(StringBuilder output, byte b)
    {
        const string hex = "0123456789ABCDEF";
        output.Append('%').Append(hex[b >> 4]).Append(hex[b & 0xF]);
    }

    /// <summary>
    /// Decodes <c>%XX</c> escapes to bytes, then the bytes as UTF-8, replacing
    /// invalid sequences. A <c>%</c> not followed by two hex digits is literal.
    /// </summary>
    public static string Decode(string input) => Encoding.UTF8.GetString(DecodeBytes(input));

    public static byte[] DecodeBytes(string input)
    {
        var raw = Encoding.UTF8.GetBytes(input);
        var output = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'%' && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
            {
                output.Add((byte)((HexValue(raw[i + 1]) << 4) | HexValue(raw[i + 2])));
                i += 2;
            }
            else
            {
                output.Add(raw[i]);
            }
        }
        return [.. output];
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => b - 'A' + 10,
    };

    /// <summary>
    /// <c>application/x-www-form-urlencoded</c> byte serialization, per the URL
    /// Standard: alphanumerics and <c>*-._</c>
    /// pass, a space becomes <c>+</c>, everything else is escaped.
    /// </summary>
    public static string FormEncode(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var b in Encoding.UTF8.GetBytes(ReplaceLoneSurrogates(input)))
        {
            if (b < 128 && (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_'))
            {
                output.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                output.Append('+');
            }
            else
            {
                AppendByte(output, b);
            }
        }
        return output.ToString();
    }

    /// <summary>A form body or query: <c>name=value</c> pairs joined with <c>&amp;</c>.</summary>
    public static string FormSerialize(IEnumerable<(string Name, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => FormEncode(p.Name) + "=" + FormEncode(p.Value)));

    /// <summary>
    /// Parses <c>application/x-www-form-urlencoded</c> text, per the URL
    /// Standard: empty pieces are skipped, <c>+</c> is a space.
    /// </summary>
    public static List<(string Name, string Value)> FormParse(string input)
    {
        var pairs = new List<(string, string)>();
        foreach (var piece in input.Split('&'))
        {
            if (piece.Length == 0)
            {
                continue;
            }
            var eq = piece.IndexOf('=');
            var name = eq < 0 ? piece : piece[..eq];
            var value = eq < 0 ? "" : piece[(eq + 1)..];
            pairs.Add((Decode(name.Replace('+', ' ')), Decode(value.Replace('+', ' '))));
        }
        return pairs;
    }

    internal static string ReplaceLoneSurrogates(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            if (!char.IsSurrogate(input[i]))
            {
                continue;
            }
            if (char.IsHighSurrogate(input[i]) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                i++;
                continue;
            }
            var chars = input.ToCharArray();
            for (var j = i; j < chars.Length; j++)
            {
                if (char.IsHighSurrogate(chars[j]) && j + 1 < chars.Length && char.IsLowSurrogate(chars[j + 1]))
                {
                    j++;
                }
                else if (char.IsSurrogate(chars[j]))
                {
                    chars[j] = '�';
                }
            }
            return new string(chars);
        }
        return input;
    }
}
