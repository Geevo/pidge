using System.Text;

namespace Pidge.Core.Idna;

/// <summary>Punycode (RFC 3492), for one label at a time.</summary>
internal static class Punycode
{
    private const int Base = 36;
    private const int TMin = 1;
    private const int TMax = 26;
    private const int Skew = 38;
    private const int Damp = 700;
    private const int InitialBias = 72;
    private const int InitialN = 0x80;

    /// <summary>
    /// Decodes the part of a label after <c>xn--</c>. Basic code points come out
    /// lower-cased. Upper-case digits are accepted only when
    /// <paramref name="allowUpperCase"/> is set, which is when the label was
    /// typed rather than produced by mapping (mapping lower-cases everything).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<int> input, bool allowUpperCase, out List<int> output)
    {
        output = [];
        var delimiter = input.LastIndexOf('-');
        ReadOnlySpan<int> basic = delimiter >= 0 ? input[..delimiter] : [];
        // A delimiter in first place marks no basic code points, and is then
        // read as a digit, which it is not.
        var extended = delimiter > 0 ? input[(delimiter + 1)..] : input;

        foreach (var c in basic)
        {
            output.Add(c is >= 'A' and <= 'Z' ? c + 0x20 : c);
        }

        long length = basic.Length;
        long codePoint = InitialN;
        var bias = InitialBias;
        long i = 0;
        var position = 0;
        while (position < extended.Length)
        {
            var previous = i;
            long weight = 1;
            for (var k = Base; ; k += Base)
            {
                if (position == extended.Length)
                {
                    return false;
                }

                var digit = DigitOf(extended[position++], allowUpperCase);
                if (digit < 0)
                {
                    return false;
                }

                i += digit * weight;
                if (i > uint.MaxValue)
                {
                    return false;
                }

                var t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                if (digit < t)
                {
                    break;
                }

                weight *= Base - t;
                if (weight > uint.MaxValue)
                {
                    return false;
                }
            }

            bias = Adapt(i - previous, length + 1, previous == 0);
            codePoint += i / (length + 1);
            i %= length + 1;
            if (codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
            {
                return false;
            }

            output.Insert((int)i, (int)codePoint);
            length++;
            i++;
        }

        return true;
    }

    public static void Encode(ReadOnlySpan<int> input, StringBuilder output)
    {
        var basicLength = 0;
        foreach (var c in input)
        {
            if (c < 0x80)
            {
                output.Append((char)c);
                basicLength++;
            }
        }

        if (basicLength > 0)
        {
            output.Append('-');
        }

        var codePoint = InitialN;
        long delta = 0;
        var bias = InitialBias;
        var processed = basicLength;
        while (processed < input.Length)
        {
            var next = int.MaxValue;
            foreach (var c in input)
            {
                if (c >= codePoint && c < next)
                {
                    next = c;
                }
            }

            delta += (long)(next - codePoint) * (processed + 1);
            codePoint = next;
            foreach (var c in input)
            {
                if (c < codePoint)
                {
                    delta++;
                }

                if (c != codePoint)
                {
                    continue;
                }

                var q = delta;
                for (var k = Base; ; k += Base)
                {
                    var t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                    if (q < t)
                    {
                        break;
                    }

                    output.Append(DigitChar((int)(t + ((q - t) % (Base - t)))));
                    q = (q - t) / (Base - t);
                }

                output.Append(DigitChar((int)q));
                bias = Adapt(delta, processed + 1, processed == basicLength);
                delta = 0;
                processed++;
            }

            delta++;
            codePoint++;
        }
    }

    private static int Adapt(long delta, long points, bool firstTime)
    {
        delta /= firstTime ? Damp : 2;
        delta += delta / points;
        var k = 0;
        while (delta > (Base - TMin) * TMax / 2)
        {
            delta /= Base - TMin;
            k += Base;
        }

        return (int)(k + ((Base - TMin + 1) * delta / (delta + Skew)));
    }

    private static int DigitOf(int c, bool allowUpperCase) => c switch
    {
        >= '0' and <= '9' => c - '0' + 26,
        >= 'a' and <= 'z' => c - 'a',
        >= 'A' and <= 'Z' when allowUpperCase => c - 'A',
        _ => -1,
    };

    private static char DigitChar(int value) => (char)(value < 26 ? 'a' + value : '0' + value - 26);
}
