using System.Globalization;
using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Shared spelling: the pieces every generator needs, so the languages cannot
/// disagree about what the same request is.
/// </summary>
internal static class Text
{
    /// <summary>
    /// A timeout as seconds, for languages that count in them. No trailing
    /// zeros: 30000 is <c>30</c>, 1500 is <c>1.5</c>.
    /// </summary>
    public static string Seconds(ulong milliseconds)
    {
        if (milliseconds % 1000 == 0)
        {
            return (milliseconds / 1000).ToString(CultureInfo.InvariantCulture);
        }
        return Float(milliseconds / 1000.0);
    }

    /// <summary>
    /// A float the way a plain <c>{}</c> prints one: the shortest digits that
    /// read back as the same number, never in exponent form, and no <c>.0</c>
    /// on a whole number.
    /// </summary>
    public static string Float(double value)
    {
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = shortest.IndexOf('E');
        if (exponentAt < 0)
        {
            return shortest;
        }

        var negative = shortest.StartsWith('-');
        var mantissa = shortest[(negative ? 1 : 0)..exponentAt];
        var exponent = int.Parse(shortest[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var pointAfter = (dot < 0 ? mantissa.Length : dot) + exponent;

        string positional;
        if (pointAfter <= 0)
        {
            positional = "0." + new string('0', -pointAfter) + digits;
        }
        else if (pointAfter >= digits.Length)
        {
            positional = digits + new string('0', pointAfter - digits.Length);
        }
        else
        {
            positional = digits[..pointAfter] + "." + digits[pointAfter..];
        }
        return negative ? "-" + positional : positional;
    }

    /// <summary>A form body, encoded exactly as the engine encodes it.</summary>
    public static string FormEncoded(IEnumerable<KeyValueEntry> entries) => RequestPlanning.FormBody(entries);

    /// <summary>
    /// Headers as a dictionary can hold them: one entry per name.
    ///
    /// Python takes a repeated key and keeps the last; PowerShell will not parse
    /// a hash literal with one at all. Folding repeats into a comma-separated
    /// value is what HTTP itself says two rows of the same name mean, and keeps
    /// both. The flag is for the note that says so.
    /// </summary>
    public static (List<(string Name, string Value)> Folded, bool Collided) Fold(
        IEnumerable<(string Name, string Value)> headers)
    {
        var folded = new List<(string Name, string Value)>();
        var collided = false;

        foreach (var (name, value) in headers)
        {
            var index = folded.FindIndex(existing => AsciiEquals(existing.Name, name));
            if (index >= 0)
            {
                folded[index] = (folded[index].Name, folded[index].Value + ", " + value);
                collided = true;
            }
            else
            {
                folded.Add((name, value));
            }
        }

        return (folded, collided);
    }

    /// <summary>The note <see cref="Fold"/> earns when it had to.</summary>
    public const string FoldedNote =
        "Two header rows share a name. They are sent here as one comma-separated value, "
        + "which is what a repeated header means.";

    /// <summary>
    /// Pulls <c>Content-Type</c> out of a header list, for the languages that
    /// set it somewhere other than with the rest — .NET puts it on the content,
    /// and PowerShell takes it as its own parameter.
    /// </summary>
    public static string? TakeContentType(List<(string Name, string Value)> headers)
    {
        var index = headers.FindIndex(header => AsciiEquals(header.Name, "content-type"));
        if (index < 0)
        {
            return null;
        }
        var value = headers[index].Value;
        headers.RemoveAt(index);
        return value;
    }

    /// <summary>
    /// A note as comment lines, wrapped so none of them runs off the pane.
    ///
    /// <paramref name="prefix"/> is the comment marker and the space after it:
    /// <c># </c> or <c>// </c>.
    ///
    /// A line of the note that starts with a space is left exactly as it is.
    /// That is for a command somebody is meant to copy: wrapping one turns it
    /// into three lines that cannot be pasted, which is worse than a line that
    /// runs long.
    /// </summary>
    public static string Comment(string prefix, string note) =>
        string.Join(
            "\n",
            Lines(note).Select(line =>
                line.Length > 0 && char.IsWhiteSpace(line[0]) ? prefix + line : Wrap(prefix, line)));

    private static string Wrap(string prefix, string note)
    {
        const int width = 80;
        var lines = new List<string>();
        var line = new StringBuilder();
        var lineBytes = 0;
        var prefixBytes = Utf8Length(prefix);

        foreach (var word in SplitWhitespace(note))
        {
            var wordBytes = Utf8Length(word);
            var candidate = line.Length == 0 ? wordBytes + prefixBytes : lineBytes + prefixBytes + 1 + wordBytes;
            if (line.Length > 0 && candidate > width)
            {
                lines.Add(prefix + line);
                line.Clear();
                lineBytes = 0;
            }
            if (line.Length > 0)
            {
                line.Append(' ');
                lineBytes++;
            }
            line.Append(word);
            lineBytes += wordBytes;
        }
        if (line.Length > 0)
        {
            lines.Add(prefix + line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Marks a line as the inside of a multi-line string literal.
    ///
    /// The body of a request is carried into the snippet verbatim, and in a
    /// language whose code sits inside a function the snippet is then indented —
    /// which would put four spaces inside the body and send something nobody
    /// wrote. A literal says where it begins and ends, rather than the indenter
    /// guessing, and the mark is taken out again once everything has been laid out.
    /// </summary>
    public const char KeepMark = '\u0001';

    /// <summary>
    /// Marks every line of <paramref name="block"/> after the first, which is
    /// where a literal's own content starts.
    /// </summary>
    public static string Keep(string block)
    {
        var lines = Lines(block);
        var first = lines.Count > 0 ? lines[0] : "";
        if (lines.Count <= 1)
        {
            return first;
        }
        return first + "\n" + string.Join("\n", lines.Skip(1).Select(line => KeepMark + line));
    }

    /// <summary>Takes the marks out, once nothing is going to be indented again.</summary>
    public static string Settle(string block) => block.Replace(KeepMark.ToString(), "", StringComparison.Ordinal);

    /// <summary>
    /// Indents every line of a block by <paramref name="spaces"/>, leaving blank
    /// lines blank and leaving the inside of a literal exactly where it is.
    /// </summary>
    public static string Indent(string block, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join(
            "\n",
            Lines(block).Select(line => line.Length == 0 || line[0] == KeepMark ? line : pad + line));
    }

    /// <summary>
    /// The file name a multipart part is sent under, which is the path's own
    /// last component when nothing else was given. The same rule the engine uses.
    /// </summary>
    public static string FileName(string path, string? given)
    {
        if (given is not null)
        {
            return given;
        }
        return PathName(path) is { } span ? path.Substring(span.Start, span.Length) : "file";
    }

    /// <summary>
    /// The path with its extension replaced, or added where it had none — the
    /// file stem kept, and everything after it dropped.
    /// </summary>
    public static string WithExtension(string path, string extension)
    {
        if (PathName(path) is not { } span)
        {
            return path;
        }
        var name = path.Substring(span.Start, span.Length);
        var lastDot = name.LastIndexOf('.');
        var stemLength = lastDot <= 0 ? name.Length : lastDot;
        var kept = path[..(span.Start + stemLength)];
        return extension.Length == 0 ? kept : kept + "." + extension;
    }

    /// <summary>
    /// Where the last component of a path is, when it is a name rather than a
    /// root, a drive, <c>.</c> or <c>..</c>. Trailing separators and <c>.</c>
    /// components are passed over. A backslash separates only on Windows.
    /// </summary>
    private static (int Start, int Length)? PathName(string path)
    {
        var windows = OperatingSystem.IsWindows();
        bool IsSeparator(char c) => c == '/' || (windows && c == '\\');

        var bodyStart = windows ? WindowsPrefixLength(path) : 0;
        var end = path.Length;
        while (end > bodyStart)
        {
            var start = end;
            while (start > bodyStart && !IsSeparator(path[start - 1]))
            {
                start--;
            }
            var length = end - start;
            if (length == 0 || (length == 1 && path[start] == '.'))
            {
                end = start - 1;
                continue;
            }
            if (length == 2 && path[start] == '.' && path[start + 1] == '.')
            {
                return null;
            }
            return (start, length);
        }
        return null;
    }

    /// <summary>A drive (<c>C:</c>) or a share (<c>\\server\share</c>) at the front of a Windows path.</summary>
    private static int WindowsPrefixLength(string path)
    {
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return 2;
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var server = path.IndexOf('\\', 2);
            if (server < 0)
            {
                return path.Length;
            }
            var share = path.IndexOf('\\', server + 1);
            return share < 0 ? path.Length : share;
        }
        return 0;
    }

    /// <summary>
    /// The lines of a string, split at <c>\n</c> with a <c>\r</c> before it
    /// dropped. A final newline does not start another line, and an empty
    /// string has none.
    /// </summary>
    public static List<string> Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(text[start..]);
                break;
            }
            var end = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(text[start..end]);
            start = newline + 1;
        }
        return lines;
    }

    /// <summary>The words of a string, however much whitespace is between them.</summary>
    public static IEnumerable<string> SplitWhitespace(string text)
    {
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0)
                {
                    yield return text[start..i];
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        if (start >= 0)
        {
            yield return text[start..];
        }
    }

    /// <summary>The first whitespace character, splitting the string either side of it.</summary>
    public static (string Before, string After)? SplitOnceWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return (text[..i], text[(i + 1)..]);
            }
        }
        return null;
    }

    /// <summary>The first <paramref name="separator"/>, splitting the string either side of it.</summary>
    public static (string Before, string After)? SplitOnce(string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index < 0 ? null : (text[..index], text[(index + 1)..]);
    }

    /// <summary>Letter case ignored for ASCII only, as HTTP compares names.</summary>
    public static bool AsciiEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (AsciiLower(a[i]) != AsciiLower(b[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    public static string AsciiLower(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = AsciiLower(source[i]);
        }
    });

    /// <summary>How long the text is in UTF-8, which is what the widths here are counted in.</summary>
    public static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// Left-aligns <paramref name="text"/> in a column <paramref name="width"/>
    /// characters wide, counting characters rather than UTF-16 units.
    /// </summary>
    public static string PadRight(string text, int width)
    {
        var characters = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            characters++;
        }
        return characters >= width ? text : text + new string(' ', width - characters);
    }

    /// <summary>The text with every trailing copy of <paramref name="suffix"/> removed.</summary>
    public static string TrimEndMatches(string text, string suffix)
    {
        while (suffix.Length > 0 && text.EndsWith(suffix, StringComparison.Ordinal))
        {
            text = text[..^suffix.Length];
        }
        return text;
    }

    /// <summary>The text with every leading and trailing <paramref name="c"/> removed.</summary>
    public static string TrimMatches(string text, char c) => text.Trim(c);
}
