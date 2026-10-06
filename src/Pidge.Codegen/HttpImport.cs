using System.Text;
using System.Text.Unicode;
using Pidge.Core;

namespace Pidge.Codegen;

/// <summary>
/// Reading a <c>.http</c> file back into requests: the part of the format that
/// VS Code's REST Client and JetBrains' HTTP Client agree on.
///
/// That is a <c>###</c> line between requests, a request line, headers, a
/// blank line and a body, with <c>#</c> and <c>//</c> comments. File variables
/// (<c>@name = value</c>) are substituted in, since the file that defined them
/// is not coming along. Scripts, response handlers and anything else one client
/// adds are left behind, and a request that cannot come across whole is skipped
/// with a sentence saying why rather than half-imported.
/// </summary>
internal static class HttpImport
{
    public static ParsedHttpFile Parse(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var parsed = new ParsedHttpFile();

        foreach (var block in Blocks(text))
        {
            switch (ParseBlock(block, variables))
            {
                case { Request: { } request }:
                    parsed.Requests.Add(request);
                    break;
                case { Skipped: { } reason }:
                    parsed.Skipped.Add(reason);
                    break;
            }
        }
        return parsed;
    }

    private sealed class Block(string? title)
    {
        /// <summary>The text after <c>###</c>, if there was any.</summary>
        public string? Title { get; } = title;

        public List<string> Lines { get; } = [];
    }

    private readonly record struct Outcome(ParsedRequest? Request, string? Skipped);

    private static List<Block> Blocks(string text)
    {
        var blocks = new List<Block> { new(null) };
        foreach (var line in Text.Lines(text))
        {
            var start = line.TrimStart();
            if (start.StartsWith("###", StringComparison.Ordinal))
            {
                var title = start[3..].Trim();
                blocks.Add(new Block(title.Length == 0 ? null : title));
            }
            else
            {
                blocks[^1].Lines.Add(line);
            }
        }
        return blocks;
    }

    private static bool IsComment(string line)
    {
        line = line.TrimStart();
        return line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing for a block with no request in it: the part before the first
    /// <c>###</c>, or one holding only comments and variables.
    /// </summary>
    private static Outcome? ParseBlock(Block block, SortedDictionary<string, string> variables)
    {
        var lines = block.Lines;
        var at = 0;
        var name = block.Title;

        // Everything before the request line: blanks, comments, `# @name`,
        // `@variable = value`, and a pre-request script.
        string requestLine;
        while (true)
        {
            if (at >= lines.Count)
            {
                return null;
            }
            var trimmed = lines[at++].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (NameTag(trimmed) is { } tag)
            {
                name ??= tag.Trim().TrimStart('=').Trim();
                continue;
            }
            if (IsComment(trimmed))
            {
                continue;
            }
            if (FileVariable(trimmed) is (var variable, var value))
            {
                variables[variable] = Substitute(value, variables);
                continue;
            }
            if (trimmed.StartsWith("< {%", StringComparison.Ordinal))
            {
                at = SkipScript(trimmed, lines, at);
                continue;
            }
            requestLine = trimmed;
            break;
        }

        if (RequestLineParts(requestLine) is not (var method, var url))
        {
            var unknown = FirstWord(requestLine).First;
            return new Outcome(
                null,
                $"“{name ?? requestLine}” was skipped: {unknown} is not a method this app sends.");
        }

        // A query spread over indented `?` and `&` lines.
        while (at < lines.Count)
        {
            var next = lines[at];
            var continued = next.TrimStart();
            if (next.Length > 0 && char.IsWhiteSpace(next[0])
                && (continued.StartsWith('?') || continued.StartsWith('&')))
            {
                url += continued.TrimEnd();
                at++;
            }
            else
            {
                break;
            }
        }

        var headers = new List<(string Name, string Value)>();
        while (at < lines.Count)
        {
            var trimmed = lines[at++].Trim();
            if (trimmed.Length == 0)
            {
                break;
            }
            if (IsComment(trimmed))
            {
                continue;
            }
            if (Text.SplitOnce(trimmed, ':') is (var header, var value))
            {
                headers.Add((header.Trim(), value.Trim()));
            }
        }

        var bodyLines = new List<string>();
        for (; at < lines.Count; at++)
        {
            var line = lines[at];
            var trimmed = line.TrimStart();
            // A response handler or a reference to a saved response ends the body.
            if (trimmed.StartsWith("> ", StringComparison.Ordinal)
                || trimmed.StartsWith(">>", StringComparison.Ordinal)
                || trimmed.StartsWith("<> ", StringComparison.Ordinal))
            {
                break;
            }
            bodyLines.Add(line);
        }
        while (bodyLines.Count > 0 && bodyLines[^1].Trim().Length == 0)
        {
            bodyLines.RemoveAt(bodyLines.Count - 1);
        }
        var body = string.Join("\n", bodyLines);

        url = Substitute(url, variables);
        headers = headers.Select(header => (header.Name, Substitute(header.Value, variables))).ToList();
        body = Substitute(body, variables);

        return new Outcome(
            new ParsedRequest(name ?? $"{method.AsString()} {url}", Build(method, url, headers, body)),
            null);
    }

    /// <summary>The value of a <c># @name</c> or <c>// @name</c> line, before it is tidied.</summary>
    private static string? NameTag(string trimmed)
    {
        string rest;
        if (trimmed.StartsWith('#'))
        {
            rest = trimmed[1..];
        }
        else if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            rest = trimmed[2..];
        }
        else
        {
            return null;
        }
        rest = rest.Trim();
        return rest.StartsWith("@name", StringComparison.Ordinal) ? rest[5..] : null;
    }

    private static (string Name, string Value)? FileVariable(string line)
    {
        if (!line.StartsWith('@') || Text.SplitOnce(line[1..], '=') is not (var name, var value))
        {
            return null;
        }
        name = name.Trim();
        var valid = name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
        return valid ? (name, value.Trim()) : null;
    }

    /// <summary>Past the <c>%}</c> that closes a script, however many lines it takes.</summary>
    private static int SkipScript(string first, List<string> lines, int at)
    {
        if (first.Contains("%}", StringComparison.Ordinal))
        {
            return at;
        }
        while (at < lines.Count)
        {
            if (lines[at++].Contains("%}", StringComparison.Ordinal))
            {
                return at;
            }
        }
        return at;
    }

    /// <summary>The first word of a line, and the rest of it trimmed.</summary>
    private static (string First, string Remainder) FirstWord(string line) =>
        Text.SplitOnceWhitespace(line) is (var first, var rest) ? (first, rest.Trim()) : (line, "");

    /// <summary>The method and URL, or nothing when the method is one this app does not know.</summary>
    private static (RequestMethod Method, string Url)? RequestLineParts(string line)
    {
        var (first, rest) = FirstWord(line);

        var looksLikeMethod = first.Length > 0 && first.All(char.IsAsciiLetterUpper) && rest.Length > 0;
        RequestMethod method;
        string url;
        if (looksLikeMethod)
        {
            if (RequestMethodExtensions.Parse(first) is not { } known)
            {
                return null;
            }
            (method, url) = (known, rest);
        }
        else
        {
            // Both clients take a bare URL as a GET.
            (method, url) = (RequestMethod.Get, line);
        }

        // `GET /users HTTP/1.1`: the version is the client's business.
        for (var i = url.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(url[i]))
            {
                if (url[(i + 1)..].StartsWith("HTTP/", StringComparison.Ordinal))
                {
                    url = url[..i].TrimEnd();
                }
                break;
            }
        }
        return (method, url);
    }

    /// <summary>
    /// Replaces <c>{{name}}</c> for every file variable; anything else,
    /// including <c>{{$guid}}</c> and the rest of the dynamic ones, is left for
    /// the app.
    /// </summary>
    private static string Substitute(string text, SortedDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
        {
            text = text.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        }
        return text;
    }

    private static HttpRequest Build(RequestMethod method, string url, List<(string Name, string Value)> headers, string body)
    {
        var request = HttpRequest.Blank();
        request.Method = method;
        request.QueryParams = QueryParams(url);
        request.Url = url;

        if (Position(headers, "Authorization") is { } index && AuthFrom(headers[index].Value) is { } auth)
        {
            request.Auth = auth;
            headers.RemoveAt(index);
        }

        var contentType = Position(headers, "Content-Type") is { } at ? headers[at].Value : null;
        var (parsedBody, headerIsImplied) = BodyFrom(body, contentType);
        request.Body = parsedBody;
        if (headerIsImplied && Position(headers, "Content-Type") is { } implied)
        {
            headers.RemoveAt(implied);
        }

        request.Headers = headers.Select(header => KeyValueEntry.New(header.Name, header.Value)).ToList();
        return request;
    }

    private static int? Position(List<(string Name, string Value)> headers, string name)
    {
        var index = headers.FindIndex(header => Text.AsciiEquals(header.Name, name));
        return index < 0 ? null : index;
    }

    private static List<KeyValueEntry> QueryParams(string url)
    {
        if (Text.SplitOnce(url, '?') is not (_, var query))
        {
            return [];
        }
        var hash = query.IndexOf('#');
        if (hash >= 0)
        {
            query = query[..hash];
        }
        return query
            .Split('&')
            .Where(pair => pair.Length > 0)
            .Select(pair =>
            {
                var (name, value) = Text.SplitOnce(pair, '=') ?? (pair, "");
                return KeyValueEntry.New(Decode(name, false), Decode(value, false));
            })
            .ToList();
    }

    private static string Decode(string text, bool plusIsSpace) =>
        PercentEncoding.Decode(plusIsSpace ? text.Replace('+', ' ') : text);

    /// <summary>
    /// The Auth tab's version of an <c>Authorization</c> header, where there is
    /// one. Both clients accept <c>Basic user password</c> as well as the
    /// encoded form.
    /// </summary>
    private static AuthConfig? AuthFrom(string value)
    {
        if (Text.SplitOnceWhitespace(value.Trim()) is not (var scheme, var rest))
        {
            return null;
        }
        rest = rest.Trim();
        if (Text.AsciiEquals(scheme, "bearer"))
        {
            return new BearerAuth { Token = rest };
        }
        if (Text.AsciiEquals(scheme, "basic"))
        {
            return Pair(rest) is (var username, var password)
                ? new BasicAuth { Username = username, Password = password }
                : null;
        }
        if (Text.AsciiEquals(scheme, "digest"))
        {
            return Text.SplitOnceWhitespace(rest) is (var username, var password)
                ? new DigestAuth { Username = username, Password = password.Trim() }
                : null;
        }
        return null;
    }

    private static (string Username, string Password)? Pair(string rest)
    {
        if (Text.SplitOnceWhitespace(rest) is (var username, var password))
        {
            return (username, password.Trim());
        }
        if (DecodeBase64(rest) is not { } bytes || !Utf8.IsValid(bytes))
        {
            return null;
        }
        return Text.SplitOnce(Encoding.UTF8.GetString(bytes), ':');
    }

    /// <summary>
    /// Standard base64, strictly: padded to a multiple of four, nothing outside
    /// the alphabet, and no stray bits in the final character.
    /// </summary>
    private static byte[]? DecodeBase64(string text)
    {
        if (text.Length % 4 != 0)
        {
            return null;
        }
        var padding = text.EndsWith("==", StringComparison.Ordinal) ? 2 : text.EndsWith('=') ? 1 : 0;
        var output = new List<byte>(text.Length / 4 * 3);
        Span<int> sextets = stackalloc int[4];
        for (var i = 0; i < text.Length; i += 4)
        {
            var last = i + 4 == text.Length;
            var padded = last ? padding : 0;
            for (var j = 0; j < 4; j++)
            {
                var c = text[i + j];
                if (j >= 4 - padded)
                {
                    sextets[j] = 0;
                    continue;
                }
                var sextet = c switch
                {
                    >= 'A' and <= 'Z' => c - 'A',
                    >= 'a' and <= 'z' => c - 'a' + 26,
                    >= '0' and <= '9' => c - '0' + 52,
                    '+' => 62,
                    '/' => 63,
                    _ => -1,
                };
                if (sextet < 0)
                {
                    return null;
                }
                sextets[j] = sextet;
            }
            if ((padded == 2 && (sextets[1] & 0x0F) != 0) || (padded == 1 && (sextets[2] & 0x03) != 0))
            {
                return null;
            }
            var group = (sextets[0] << 18) | (sextets[1] << 12) | (sextets[2] << 6) | sextets[3];
            output.Add((byte)(group >> 16));
            if (padded < 2)
            {
                output.Add((byte)(group >> 8));
            }
            if (padded < 1)
            {
                output.Add((byte)group);
            }
        }
        return [.. output];
    }

    /// <summary>
    /// The body, and whether its content type goes without saying — the app
    /// sets it for JSON, forms and multipart, and a copy in the headers would
    /// fight it.
    /// </summary>
    private static (RequestBody Body, bool Implied) BodyFrom(string body, string? contentType)
    {
        if (body.Trim().Length == 0)
        {
            return (new NoBody(), false);
        }
        var kind = contentType is null ? null : Text.AsciiLower(contentType.Split(';')[0].Trim());

        switch (kind)
        {
            case "application/json":
                return (new JsonBody { Text = body }, true);

            case "application/x-www-form-urlencoded":
                {
                    var joined = string.Concat(Text.Lines(body).Select(line => line.Trim()));
                    var entries = joined
                        .Split('&')
                        .Where(pair => pair.Length > 0)
                        .Select(pair =>
                        {
                            var (name, value) = Text.SplitOnce(pair, '=') ?? (pair, "");
                            return KeyValueEntry.New(Decode(name, true), Decode(value, true));
                        })
                        .ToList();
                    return (new UrlEncodedBody { Entries = entries }, true);
                }

            case "multipart/form-data":
                return Boundary(contentType!) is { } boundary
                    ? (new MultipartBody { Entries = Multipart(body, boundary) }, true)
                    : (new TextBody { Text = body, ContentType = contentType }, true);

            case not null:
                return (new TextBody { Text = body, ContentType = contentType }, true);

            default:
                var first = body.TrimStart();
                if (first.Length > 0 && first[0] is '{' or '[')
                {
                    return (new JsonBody { Text = body }, false);
                }
                return (new TextBody { Text = body, ContentType = null }, false);
        }
    }

    private static string? Boundary(string contentType)
    {
        foreach (var part in contentType.Split(';'))
        {
            if (Text.SplitOnce(part, '=') is (var key, var value) && Text.AsciiEquals(key.Trim(), "boundary"))
            {
                return Text.TrimMatches(value.Trim(), '"');
            }
        }
        return null;
    }

    private static List<MultipartEntry> Multipart(string body, string boundary)
    {
        var delimiter = $"--{boundary}";
        var entries = new List<MultipartEntry>();
        List<string>? part = null;

        foreach (var line in Text.Lines(body))
        {
            var trimmed = line.TrimEnd();
            if (trimmed == delimiter || trimmed == $"{delimiter}--")
            {
                if (part is not null && MultipartEntryFrom(part) is { } entry)
                {
                    entries.Add(entry);
                }
                part = trimmed == delimiter ? [] : null;
            }
            else
            {
                part?.Add(line);
            }
        }
        if (part is not null && MultipartEntryFrom(part) is { } last)
        {
            entries.Add(last);
        }
        return entries;
    }

    private static MultipartEntry? MultipartEntryFrom(List<string> lines)
    {
        var blank = lines.FindIndex(line => line.Trim().Length == 0);
        if (blank < 0)
        {
            return null;
        }
        var headers = lines[..blank];
        var content = lines[(blank + 1)..];

        string? name = null;
        string? fileName = null;
        string? contentType = null;
        foreach (var header in headers)
        {
            if (Text.SplitOnce(header, ':') is not (var key, var value))
            {
                continue;
            }
            if (Text.AsciiEquals(key.Trim(), "content-disposition"))
            {
                foreach (var parameter in value.Split(';'))
                {
                    if (Text.SplitOnce(parameter, '=') is (var parameterKey, var parameterValue))
                    {
                        var unquoted = Text.TrimMatches(parameterValue.Trim(), '"');
                        switch (parameterKey.Trim())
                        {
                            case "name":
                                name = unquoted;
                                break;
                            case "filename":
                                fileName = unquoted;
                                break;
                        }
                    }
                }
            }
            else if (Text.AsciiEquals(key.Trim(), "content-type"))
            {
                contentType = value.Trim();
            }
        }

        if (name is null)
        {
            return null;
        }
        while (content.Count > 0 && content[^1].Trim().Length == 0)
        {
            content.RemoveAt(content.Count - 1);
        }

        // `< path` reads a file in, in both clients.
        string? file = null;
        if (content.Count == 1)
        {
            var only = content[0].Trim();
            if (only.StartsWith('<'))
            {
                file = only[1..].Trim();
            }
        }

        MultipartValue entryValue;
        if (file is not null)
        {
            var ownName = HttpFile.LastComponent(file);
            entryValue = new MultipartFile
            {
                Path = file,
                FileName = fileName is not null && fileName != ownName ? fileName : null,
                ContentType = contentType,
            };
        }
        else
        {
            entryValue = new MultipartText { Value = string.Join("\n", content) };
        }

        return new MultipartEntry
        {
            Id = Ids.NewId(),
            Enabled = true,
            Name = name,
            Value = entryValue,
        };
    }
}
