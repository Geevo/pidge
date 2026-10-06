using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// A resolved <see cref="HttpRequest"/> turned into what goes on the wire: the
/// method, the URL, the headers in order, and the body bytes.
/// </summary>
internal sealed class PreparedRequest
{
    public required string Method { get; set; }
    public required WebUrl Url { get; set; }
    public required List<(string Name, string Value)> Headers { get; init; }
    public byte[]? Body { get; set; }
    public List<string> Warnings { get; init; } = [];

    /// <summary>Removes every header called <paramref name="name"/>, whatever its case.</summary>
    public void RemoveHeader(string name) =>
        Headers.RemoveAll(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool HasHeader(string name) =>
        Headers.Exists(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets <paramref name="name"/>, replacing whatever was there.</summary>
    public void SetHeader(string name, string value)
    {
        RemoveHeader(name);
        Headers.Add((name, value));
    }

    public PreparedRequest Clone() => new()
    {
        Method = Method,
        Url = Url.Clone(),
        Headers = [.. Headers],
        Body = Body,
        Warnings = [.. Warnings],
    };
}

internal static class Prepare
{
    /// <summary>Turns a resolved <see cref="HttpRequest"/> into something the transport can send.</summary>
    /// <exception cref="RequestErrorException">The request cannot be sent as it stands.</exception>
    public static PreparedRequest Request(HttpRequest request)
    {
        var url = UrlInput.NormalizeUrl(request.Url);
        RequestPlanning.AppendQueryParams(url, request);

        var warnings = new List<string>();
        var headers = new List<(string Name, string Value)>();

        // Credentials written into the URL go out as basic auth, and never as
        // part of the URL itself. A header the user typed replaces them.
        if (url.Username.Length > 0 || url.Password is not null)
        {
            headers.Add((RequestPlanning.Authorization, RequestPlanning.BasicCredentials(
                PercentEncoding.Decode(url.Username),
                PercentEncoding.Decode(url.Password ?? ""))));
            url = WithoutCredentials(url);
        }

        var typed = RequestPlanning.BuildHeaders(request);
        if (typed.Exists(header => header.Name.Equals(RequestPlanning.Authorization, StringComparison.OrdinalIgnoreCase)))
        {
            headers.Clear();
        }
        headers.AddRange(typed);
        var userContentType = typed.Exists(header => header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase));

        var prepared = new PreparedRequest
        {
            Method = request.Method.AsString(),
            Url = url,
            Headers = headers,
            Warnings = warnings,
        };

        ApplyAuth(prepared, request);
        ApplyBody(prepared, request, userContentType);

        return prepared;
    }

    /// <summary>The URL with its userinfo taken out.</summary>
    internal static WebUrl WithoutCredentials(WebUrl url)
    {
        var text = new StringBuilder();
        text.Append(url.Scheme).Append("://").Append(url.Host);
        if (url.Port is { } port)
        {
            text.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        text.Append(url.Path);
        if (url.Query is not null)
        {
            text.Append('?').Append(url.Query);
        }
        if (url.Fragment is not null)
        {
            text.Append('#').Append(url.Fragment);
        }
        return WebUrl.Parse(text.ToString());
    }

    private static void ApplyAuth(PreparedRequest prepared, HttpRequest request)
    {
        switch (RequestPlanning.PlanAuth(request, prepared.Warnings))
        {
            // None of these can be applied here: digest and NTLM wait for a
            // challenge, and OAuth 2 has to fetch a token first. The engine does
            // all three. A key in the query string is already in the URL.
            case AuthPlan.Nothing or AuthPlan.QueryParam or AuthPlan.Challenge or AuthPlan.OAuth2:
                break;

            case AuthPlan.Header header:
                prepared.Headers.Add((header.Name, header.Value));
                break;

            // Signed here, over the URL that is about to be requested.
            case AuthPlan.OAuth1 oauth1:
                prepared.Headers.Add((RequestPlanning.Authorization, OAuth1.Authorization(
                    request.Method.AsString(),
                    prepared.Url,
                    request.Body,
                    oauth1.Settings,
                    OAuth1.Nonce(),
                    OAuth1.Timestamp())));
                break;
        }
    }

    private static void ApplyBody(PreparedRequest prepared, HttpRequest request, bool userContentType)
    {
        // Set before the body rather than with it, so that one function decides
        // what the content type is and both the engine and the generated code
        // read the same answer.
        if (RequestPlanning.ContentTypeFor(request.Body, userContentType, prepared.Warnings) is { } contentType)
        {
            prepared.Headers.Add(("Content-Type", contentType));
        }

        switch (request.Body)
        {
            case NoBody:
                break;

            case JsonBody json:
                RequestPlanning.CheckJson(json.Text);
                prepared.Body = Encoding.UTF8.GetBytes(json.Text);
                break;

            case TextBody text:
                prepared.Body = Encoding.UTF8.GetBytes(text.Text);
                break;

            case UrlEncodedBody form:
                prepared.Body = Encoding.UTF8.GetBytes(RequestPlanning.FormBody(form.Entries));
                break;

            case MultipartBody multipart:
                var boundary = Multipart.Boundary();
                prepared.Body = Multipart.Encode(multipart.Entries, boundary);
                // Multipart writes its own, carrying the boundary it generated.
                prepared.SetHeader("Content-Type", $"multipart/form-data; boundary={boundary}");
                break;
        }
    }
}

/// <summary><c>multipart/form-data</c>, with CRLF line ends and one part per field.</summary>
internal static class Multipart
{
    /// <summary>Four random 64-bit numbers in hex, joined by dashes.</summary>
    public static string Boundary()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var parts = new string[4];
        for (var i = 0; i < 4; i++)
        {
            parts[i] = BinaryPrimitives.ReadUInt64LittleEndian(random[(i * 8)..]).ToString("x16", CultureInfo.InvariantCulture);
        }
        return string.Join('-', parts);
    }

    /// <exception cref="RequestErrorException">A file cannot be read, or a content type is not one.</exception>
    public static byte[] Encode(IEnumerable<MultipartEntry> entries, string boundary)
    {
        using var body = new MemoryStream();
        foreach (var entry in entries.Where(entry => entry.IsActive))
        {
            var name = entry.Name.Trim();
            var headers = new StringBuilder();
            headers.Append("--").Append(boundary).Append("\r\n");
            headers.Append("Content-Disposition: form-data; ").Append(Parameter("name", name));

            byte[] data;
            switch (entry.Value)
            {
                case MultipartFile file:
                    data = ReadFile(file.Path);
                    var displayName = file.FileName ?? DisplayName(file.Path);
                    headers.Append("; ").Append(FileNameParameter(displayName));
                    if (file.ContentType is { } mime)
                    {
                        if (!MediaTypeHeaderValue.TryParse(mime, out _))
                        {
                            throw new RequestError(RequestErrorKind.BodySerialization, $"`{mime}` is not a valid content type.")
                                .WithDetail("failed to parse mime type")
                                .AsException();
                        }
                        headers.Append("\r\nContent-Type: ").Append(mime);
                    }
                    break;

                case MultipartText text:
                    data = Encoding.UTF8.GetBytes(text.Value);
                    break;

                default:
                    data = [];
                    break;
            }

            headers.Append("\r\n\r\n");
            body.Write(Encoding.UTF8.GetBytes(headers.ToString()));
            body.Write(data);
            body.Write("\r\n"u8);
        }
        body.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        return body.ToArray();
    }

    /// <summary>
    /// <c>name="value"</c>, or the RFC 5987 form when anything had to be
    /// percent-encoded.
    /// </summary>
    internal static string Parameter(string name, string value)
    {
        var encoded = EncodePathSegment(value);
        return encoded.Length == Encoding.UTF8.GetByteCount(value)
            ? $"{name}=\"{value}\""
            : $"{name}*=utf-8''{encoded}";
    }

    internal static string FileNameParameter(string fileName)
    {
        var legal = fileName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\\r", StringComparison.Ordinal)
            .Replace("\n", "\\\n", StringComparison.Ordinal);
        return $"filename=\"{legal}\"";
    }

    /// <summary>Controls, non-ASCII, and <c>space " # &lt; &gt; ? ` { } / %</c>.</summary>
    internal static string EncodePathSegment(string value)
    {
        var output = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b < 0x20 || b >= 0x7F || " \"#<>?`{}/%".Contains((char)b))
            {
                output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                output.Append((char)b);
            }
        }
        return output.ToString();
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? "file" : name;
    }

    private static byte[] ReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RequestError(RequestErrorKind.Io, $"Could not read `{path}`.")
                .WithDetail(err.Message)
                .AsException();
        }
    }
}
