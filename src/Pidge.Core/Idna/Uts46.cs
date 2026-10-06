using System.Text;

namespace Pidge.Core.Idna;

/// <summary>
/// UTS 46 "ToASCII" with the options the WHATWG URL Standard uses for a host:
/// non-transitional, hyphens anywhere, no DNS length limits, the CheckBidi and
/// CheckJoiners rules on, and the URL standard's forbidden domain code points
/// refused. Any failure is one error; the URL parser reports it as an invalid
/// international domain name.
/// </summary>
public static class Uts46
{
    private const int Replacement = 0xFFFD;
    private const int MaxDecodeInput = 2000;
    private const int MaxEncodeInput = 1000;

    public static bool TryToAscii(string domain, out string ascii)
    {
        ascii = "";
        if (IsLowerCaseLettersAndDots(domain))
        {
            ascii = domain;
            return true;
        }

        // Each label after processing, as Unicode for the checks, and as typed
        // when it was typed in ASCII, which is then how it is written out.
        var labels = new List<(string? Typed, List<int> CodePoints)>();
        foreach (var raw in domain.Split('.'))
        {
            if (!TryProcess(raw, labels))
            {
                return false;
            }
        }

        if (IsBidiDomain(labels) && !labels.TrueForAll(label => SatisfiesBidiRule(label.CodePoints)))
        {
            return false;
        }

        var output = new StringBuilder(domain.Length);
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                output.Append('.');
            }

            var (typed, codePoints) = labels[i];
            if (typed is not null)
            {
                output.Append(typed);
            }
            else if (codePoints.TrueForAll(c => c < 0x80))
            {
                foreach (var c in codePoints)
                {
                    output.Append((char)c);
                }
            }
            else
            {
                output.Append("xn--");
                Punycode.Encode(codePoints.ToArray(), output);
            }
        }

        ascii = output.ToString();
        return true;
    }

    /// <summary>Processes one label as typed between ASCII dots, which mapping may split further.</summary>
    private static bool TryProcess(string raw, List<(string?, List<int>)> labels)
    {
        if (raw.All(char.IsAscii))
        {
            var typed = raw.ToLowerInvariant();
            if (typed.StartsWith("xn--", StringComparison.Ordinal))
            {
                // Checked, but written out as typed (lower-cased), not re-encoded.
                if (raw[^1] == '-' || raw.Length - 4 > MaxDecodeInput)
                {
                    return false;
                }

                var digits = raw[4..].Select(c => (int)c).ToArray();
                if (!Punycode.TryDecode(digits, allowUpperCase: true, out var decoded)
                    || !TryValidateDecoded(decoded, out var label)
                    || !PassesLabelChecks(label))
                {
                    return false;
                }

                labels.Add((typed, label));
                return true;
            }

            if (typed.Any(c => IsDenied(c)))
            {
                return false;
            }

            labels.Add((typed, typed.Select(c => (int)c).ToList()));
            return true;
        }

        var mapped = MapAndNormalize(raw);
        var start = 0;
        for (var i = 0; i <= mapped.Count; i++)
        {
            if (i < mapped.Count && mapped[i] != '.')
            {
                continue;
            }

            var label = mapped.GetRange(start, i - start);
            start = i + 1;
            if (label.Exists(c => c == Replacement || IsDenied(c)))
            {
                return false;
            }

            if (label.Count >= 4 && label[0] == 'x' && label[1] == 'n' && label[2] == '-' && label[3] == '-')
            {
                if (label.Exists(c => c >= 0x80) || label[^1] == '-' || label.Count - 4 > MaxDecodeInput)
                {
                    return false;
                }

                if (!Punycode.TryDecode(label.GetRange(4, label.Count - 4).ToArray(), allowUpperCase: false, out var decoded)
                    || !TryValidateDecoded(decoded, out label))
                {
                    return false;
                }
            }

            if (!PassesLabelChecks(label))
            {
                return false;
            }

            labels.Add((null, label));
        }

        return true;
    }

    /// <summary>
    /// A label that came out of Punycode must already be what mapping would
    /// make of it: nothing ignored, mapped or disallowed, and in NFC.
    /// </summary>
    private static bool TryValidateDecoded(List<int> decoded, out List<int> label)
    {
        var text = new StringBuilder();
        foreach (var c in decoded)
        {
            var status = IdnaTables.Status(c, out var replacement);
            switch (status)
            {
                case IdnaStatus.Valid:
                    AppendCodePoint(text, c);
                    break;
                case IdnaStatus.Mapped:
                    text.Append(replacement);
                    break;
                default:
                    AppendCodePoint(text, Replacement);
                    break;
            }
        }

        label = CodePointsOf(Normalize(text.ToString()));
        if (label.Exists(c => c == Replacement || c == '.' || IsDenied(c)))
        {
            return false;
        }

        // Compared as far as both go, as the reference implementation does.
        for (var i = 0; i < Math.Min(label.Count, decoded.Count); i++)
        {
            if (label[i] != decoded[i])
            {
                return false;
            }
        }

        return true;
    }

    private static List<int> MapAndNormalize(string raw)
    {
        var text = new StringBuilder(raw.Length);
        foreach (var rune in raw.EnumerateRunes())
        {
            var status = IdnaTables.Status(rune.Value, out var replacement);
            switch (status)
            {
                case IdnaStatus.Valid:
                    AppendCodePoint(text, rune.Value);
                    break;
                case IdnaStatus.Mapped:
                    text.Append(replacement);
                    break;
                case IdnaStatus.Ignored:
                    break;
                default:
                    AppendCodePoint(text, Replacement);
                    break;
            }
        }

        return CodePointsOf(Normalize(text.ToString()));
    }

    /// <summary>The checks each label gets once mapped: no leading mark, CONTEXTJ, and a length limit.</summary>
    private static bool PassesLabelChecks(List<int> label)
    {
        if (label.Count > 0 && IdnaTables.IsMark(label[0]))
        {
            return false;
        }

        for (var i = 0; i < label.Count; i++)
        {
            var c = label[i];
            if (c is not (0x200C or 0x200D))
            {
                continue;
            }

            if (i == 0)
            {
                return false;
            }

            if (IdnaTables.IsVirama(label[i - 1]))
            {
                continue;
            }

            // A zero-width joiner needs a virama before it; a non-joiner can
            // also sit between two letters that would otherwise join.
            if (c == 0x200D
                || !JoinsOnSide(label, i - 1, -1, JoiningType.Left)
                || !JoinsOnSide(label, i + 1, 1, JoiningType.Right))
            {
                return false;
            }
        }

        return label.TrueForAll(c => c < 0x80) || label.Count <= MaxEncodeInput;
    }

    private static bool JoinsOnSide(List<int> label, int from, int step, JoiningType wanted)
    {
        for (var i = from; i >= 0 && i < label.Count; i += step)
        {
            var type = IdnaTables.JoiningTypeOf(label[i]);
            if (type == wanted || type == JoiningType.Dual)
            {
                return true;
            }

            if (type != JoiningType.Transparent)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether any label holds a right-to-left character, which puts every label under the bidi rule.</summary>
    private static bool IsBidiDomain(List<(string? Typed, List<int> CodePoints)> labels)
    {
        foreach (var (_, label) in labels)
        {
            foreach (var c in label)
            {
                if (c < 0x0590
                    || c is >= 0x0900 and <= 0xFB1C
                    || c is >= 0x1F000 and <= 0x3FFFF
                    || c is >= 0xFF00 and <= 0x107FF
                    || c is >= 0x11000 and <= 0x1E7FF)
                {
                    continue;
                }

                if (IdnaTables.BidiClassOf(c) is BidiClass.R or BidiClass.AL or BidiClass.AN)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>RFC 5893, section 2.</summary>
    private static bool SatisfiesBidiRule(List<int> label)
    {
        if (label.Count == 0)
        {
            return true;
        }

        var first = IdnaTables.BidiClassOf(label[0]);
        if (first is not (BidiClass.L or BidiClass.R or BidiClass.AL))
        {
            return false;
        }

        var ltr = first == BidiClass.L;
        var end = label.Count;
        while (end > 1 && IdnaTables.BidiClassOf(label[end - 1]) == BidiClass.NSM)
        {
            end--;
        }

        if (end == 1)
        {
            return true;
        }

        var last = IdnaTables.BidiClassOf(label[end - 1]);
        if (ltr ? last is not (BidiClass.L or BidiClass.EN) : last is not (BidiClass.R or BidiClass.AL or BidiClass.EN or BidiClass.AN))
        {
            return false;
        }

        BidiClass? numerals = null;
        for (var i = 1; i < end - 1; i++)
        {
            var bidi = IdnaTables.BidiClassOf(label[i]);
            if (ltr)
            {
                if (bidi is not (BidiClass.L or BidiClass.EN or BidiClass.ES or BidiClass.CS or BidiClass.ET
                    or BidiClass.ON or BidiClass.BN or BidiClass.NSM))
                {
                    return false;
                }

                continue;
            }

            if (bidi is not (BidiClass.R or BidiClass.AL or BidiClass.AN or BidiClass.EN or BidiClass.ES
                or BidiClass.CS or BidiClass.ET or BidiClass.ON or BidiClass.BN or BidiClass.NSM))
            {
                return false;
            }

            if (bidi is BidiClass.EN or BidiClass.AN)
            {
                numerals ??= bidi;
                if (numerals != bidi)
                {
                    return false;
                }
            }
        }

        // European and Arabic digits may not both appear in a right-to-left label.
        return ltr || numerals is null || last is not (BidiClass.EN or BidiClass.AN) || last == numerals;
    }

    /// <summary>The URL standard's forbidden domain code points, plus the C0 controls, space and DEL.</summary>
    private static bool IsDenied(int c) => c <= 0x20 || c == 0x7F || c is '%' or '#' or '/' or ':' or '<' or '>'
        or '?' or '@' or '[' or '\\' or ']' or '^' or '|';

    private static bool IsLowerCaseLettersAndDots(string domain)
    {
        foreach (var c in domain)
        {
            if (c is not ((>= 'a' and <= 'z') or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string text) => text.IsNormalized(NormalizationForm.FormC)
        ? text
        : text.Normalize(NormalizationForm.FormC);

    private static void AppendCodePoint(StringBuilder text, int codePoint) =>
        text.Append(new Rune(codePoint).ToString());

    private static List<int> CodePointsOf(string text)
    {
        var codePoints = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            codePoints.Add(rune.Value);
        }

        return codePoints;
    }
}
