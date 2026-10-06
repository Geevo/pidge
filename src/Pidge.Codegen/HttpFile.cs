using System.Text;
using Pidge.Core;

namespace Pidge.Codegen;

/// <summary>
/// Saved requests as a <c>.http</c> file: the plain-text format that VS Code's
/// REST Client and JetBrains' HTTP Client both run.
///
/// Unlike the code targets, this writes the request as saved,
/// <c>{{variables}}</c> and all — both tools substitute them the same way the
/// app does — rather than the request as it would go on the wire. What the
/// format has no words for is said in a comment, as the code targets do.
/// </summary>
public static class HttpFile
{
    private const string Boundary = "pidge-boundary";

    /// <summary>One block per request, in the order given, each headed by its name.</summary>
    public static string Write(IEnumerable<(string Name, HttpRequest Request)> requests) =>
        string.Join("\n", requests.Select(entry => Block(entry.Name, entry.Request)));

    /// <summary>Reads a <c>.http</c> file back into requests. See <see cref="HttpImport"/>.</summary>
    public static ParsedHttpFile Parse(string text) => HttpImport.Parse(text);

    private static string Block(string name, HttpRequest request)
    {
        var headers = request.Headers
            .Where(entry => entry.IsActive)
            .Select(entry => (Name: entry.Name.Trim(), entry.Value))
            .ToList();

        var notes = new List<string>();
        var extraQuery = new List<(string Name, string Value)>();
        // A header typed by hand wins over the Auth tab, as it does on Send.
        var authorizationTyped = HasHeader(headers, "Authorization");
        switch (request.Auth)
        {
            case NoAuth:
                break;
            case not ApiKeyAuth when authorizationTyped:
                break;
            case BearerAuth bearer:
                headers.Add(("Authorization", $"Bearer {bearer.Token}"));
                break;
            case BasicAuth basic:
                headers.Add(("Authorization", Basic(basic.Username, basic.Password)));
                break;
            case DigestAuth:
                notes.Add("Digest auth is left out: a .http file has no way of saying it that both clients understand.");
                break;
            case NtlmAuth:
                notes.Add("NTLM auth is left out: a .http file has no way of saying it.");
                break;
            case OAuth1Settings:
                notes.Add("OAuth 1 signing is left out: each request is signed over itself, which a .http file cannot do.");
                break;
            case OAuth2Settings:
                notes.Add("OAuth 2 is left out: the token has to be fetched first, which a .http file cannot do.");
                break;
            case ApiKeyAuth apiKey:
                var key = apiKey.Key.Trim();
                if (key.Length > 0)
                {
                    if (apiKey.Placement == ApiKeyPlacement.Query)
                    {
                        extraQuery.Add((key, apiKey.Value));
                    }
                    else if (!HasHeader(headers, key))
                    {
                        headers.Add((key, apiKey.Value));
                    }
                }
                break;
        }

        var body = Body(request.Body, headers);

        var output = new StringBuilder();
        // `###` separates requests, and both clients show the text after it as the name.
        output.Append($"### {OneLine(name)}\n");
        foreach (var note in notes)
        {
            output.Append($"# {note}\n");
        }
        output.Append($"{request.Method.AsString()} {Url(request, extraQuery)}\n");
        foreach (var (header, value) in headers)
        {
            output.Append($"{header}: {OneLine(value)}\n");
        }
        if (body is not null)
        {
            output.Append('\n');
            output.Append(body);
            if (!body.EndsWith('\n'))
            {
                output.Append('\n');
            }
        }
        return output.ToString();
    }

    private static bool HasHeader(List<(string Name, string Value)> headers, string name) =>
        headers.Any(header => Text.AsciiEquals(header.Name, name));

    /// <summary>
    /// Both clients encode <c>Basic user password</c> themselves; that form
    /// keeps <c>{{variables}}</c> usable. Without any, the header is written
    /// finished.
    /// </summary>
    private static string Basic(string username, string password)
    {
        if (username.Contains("{{", StringComparison.Ordinal) || password.Contains("{{", StringComparison.Ordinal))
        {
            return $"Basic {username} {password}";
        }
        return $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";
    }

    /// <summary>
    /// The URL as typed, plus any active parameter it does not already carry.
    /// The table and the URL are normally one thing shown twice, so that is rare.
    /// </summary>
    private static string Url(HttpRequest request, List<(string Name, string Value)> extra)
    {
        var url = new StringBuilder(request.Url.Trim());
        var inUrl = new HashSet<(string, string)>();
        if (Text.SplitOnce(request.Url.Trim(), '?') is (_, var query))
        {
            foreach (var pair in query.Split('&'))
            {
                inUrl.Add(DecodePair(pair));
            }
        }

        var missing = request.QueryParams
            .Where(entry => entry.IsActive)
            .Select(entry => (Name: entry.Name.Trim(), entry.Value))
            .Where(pair => !inUrl.Contains((pair.Name, pair.Value)))
            .Concat(extra)
            .ToList();

        for (var index = 0; index < missing.Count; index++)
        {
            var (name, value) = missing[index];
            var separator = index == 0 && !url.ToString().Contains('?') ? '?' : '&';
            url.Append(separator);
            url.Append(Encode(name, request.EncodeQuery, QueryEncode));
            url.Append('=');
            url.Append(Encode(value, request.EncodeQuery, QueryEncode));
        }
        return url.ToString();
    }

    private static (string, string) DecodePair(string pair)
    {
        var (name, value) = Text.SplitOnce(pair, '=') ?? (pair, "");
        return (PercentEncoding.Decode(name), PercentEncoding.Decode(value));
    }

    /// <summary>
    /// The body as the file will carry it, adding the content type it implies
    /// when no header says otherwise.
    /// </summary>
    private static string? Body(RequestBody body, List<(string Name, string Value)> headers)
    {
        void ContentType(string value)
        {
            if (!HasHeader(headers, "Content-Type"))
            {
                headers.Add(("Content-Type", value));
            }
        }

        switch (body)
        {
            case JsonBody json:
                if (json.Text.Trim().Length == 0)
                {
                    return null;
                }
                ContentType("application/json");
                return json.Text;

            case TextBody text:
                if (text.Text.Length == 0)
                {
                    return null;
                }
                if (text.ContentType is { } kind && kind.Trim().Length > 0)
                {
                    ContentType(kind);
                }
                return text.Text;

            case UrlEncodedBody form:
                {
                    var active = form.Entries.Where(entry => entry.IsActive).ToList();
                    if (active.Count == 0)
                    {
                        return null;
                    }
                    ContentType("application/x-www-form-urlencoded");
                    return string.Join(
                        "&",
                        active.Select(entry => $"{Encode(entry.Name.Trim(), true, FormEncode)}={Encode(entry.Value, true, FormEncode)}"));
                }

            case MultipartBody multipart:
                {
                    var active = multipart.Entries.Where(entry => entry.IsActive).ToList();
                    if (active.Count == 0)
                    {
                        return null;
                    }
                    ContentType($"multipart/form-data; boundary={Boundary}");
                    var output = new StringBuilder();
                    foreach (var entry in active)
                    {
                        output.Append($"--{Boundary}\n");
                        switch (entry.Value)
                        {
                            case MultipartText text:
                                output.Append($"Content-Disposition: form-data; name=\"{entry.Name.Trim()}\"\n\n{text.Value}\n");
                                break;
                            case MultipartFile file:
                                var fileName = file.FileName ?? LastComponent(file.Path);
                                output.Append(
                                    $"Content-Disposition: form-data; name=\"{entry.Name.Trim()}\"; filename=\"{fileName}\"\n");
                                if (file.ContentType is { } partType)
                                {
                                    output.Append($"Content-Type: {partType}\n");
                                }
                                // `<` reads the file in, in both clients.
                                output.Append($"\n< {file.Path}\n");
                                break;
                        }
                    }
                    output.Append($"--{Boundary}--\n");
                    return output.ToString();
                }

            default:
                return null;
        }
    }

    /// <summary>What follows the last <c>/</c> or <c>\</c>, whichever platform wrote the path.</summary>
    internal static string LastComponent(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>RFC 3986's unreserved set, as the engine encodes a query.</summary>
    private static string QueryEncode(string text) => PercentEncoding.Encode(text, EncodeSet.Unreserved);

    /// <summary>
    /// What <c>application/x-www-form-urlencoded</c> escapes, with a space
    /// written as <c>%20</c>: <see cref="PercentEncoding.FormEncode"/> writes it
    /// as <c>+</c> and a literal <c>+</c> as <c>%2B</c>, so every <c>+</c> it
    /// leaves is a space.
    /// </summary>
    private static string FormEncode(string text) =>
        PercentEncoding.FormEncode(text).Replace("+", "%20", StringComparison.Ordinal);

    /// <summary>
    /// Percent-encodes everything but <c>{{variables}}</c>, which the client has
    /// to see intact to substitute them.
    /// </summary>
    private static string Encode(string text, bool enabled, Func<string, string> encode)
    {
        if (!enabled)
        {
            return text;
        }
        var output = new StringBuilder();
        var rest = text;
        while (rest.IndexOf("{{", StringComparison.Ordinal) is var start and >= 0)
        {
            var length = rest[start..].IndexOf("}}", StringComparison.Ordinal);
            if (length < 0)
            {
                break;
            }
            output.Append(encode(rest[..start]));
            output.Append(rest, start, length + 2);
            rest = rest[(start + length + 2)..];
        }
        output.Append(encode(rest));
        return output.ToString();
    }

    /// <summary>A header value or a name that spans lines would end the block early.</summary>
    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
