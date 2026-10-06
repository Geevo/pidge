using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pidge.TestServer;

/// <summary>A parsed request. Header names are lower-cased.</summary>
internal sealed class Request
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public string Query { get; init; } = "";
    public List<(string Name, string Value)> Headers { get; init; } = [];
    public byte[] Body { get; set; } = [];

    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
        {
            if (key == name)
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>The query as a map; a name sent twice keeps its last value.</summary>
    public Dictionary<string, string> QueryPairs()
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Query.Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }
            var equals = pair.IndexOf('=');
            if (equals >= 0)
            {
                pairs[Http.Decode(pair[..equals])] = Http.Decode(pair[(equals + 1)..]);
            }
            else
            {
                pairs[Http.Decode(pair)] = "";
            }
        }
        return pairs;
    }

    public string BodyText() => Encoding.UTF8.GetString(Body);

    /// <summary>The path split into non-empty segments.</summary>
    public string[] Segments() => Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>What a route decided to send back.</summary>
internal sealed class Response(int status, string reason)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public List<(string Name, string Value)> Headers { get; } = [];
    public byte[] Body { get; private set; } = [];

    public static Response Ok() => new(200, "OK");

    public Response Header(string name, string value)
    {
        Headers.Add((name, value));
        return this;
    }

    public Response WithBody(byte[] body)
    {
        Body = body;
        return this;
    }

    public Response Text(string text) =>
        Header("content-type", "text/plain; charset=utf-8").WithBody(Encoding.UTF8.GetBytes(text));

    public Response Json(Action<Utf8JsonWriter> write) =>
        Header("content-type", "application/json").WithBody(Http.Json(write));
}

/// <summary>
/// What one connection remembers between requests.
///
/// NTLM authenticates the connection rather than the request, so a server has
/// to hold its challenge here. Everything else is stateless.
/// </summary>
internal sealed class ConnectionState
{
    public byte[]? NtlmChallenge { get; set; }
    public int Requests { get; set; }
}

/// <summary>A buffered reader over the connection, for a request head read byte by byte.</summary>
internal sealed class ConnectionReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position;
    private int _length;

    public Stream Stream { get; } = stream;

    /// <summary>The next byte, or -1 at the end of the stream.</summary>
    public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position == _length)
        {
            _length = await Stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;
            if (_length == 0)
            {
                return -1;
            }
        }
        return _buffer[_position++];
    }

    public async ValueTask ReadExactAsync(byte[] target, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < target.Length)
        {
            if (_position == _length)
            {
                _length = await Stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    throw new EndOfStreamException();
                }
            }
            var take = Math.Min(target.Length - filled, _length - _position);
            Array.Copy(_buffer, _position, target, filled, take);
            _position += take;
            filled += take;
        }
    }
}

internal static class Http
{
    public static async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new ConnectionReader(stream);
        var state = new ConnectionState();

        // Keep-alive, so a client can hold a connection across several requests —
        // which is the only way an NTLM handshake can be tested at all.
        while (true)
        {
            var request = await ReadRequestAsync(reader, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }
            state.Requests++;

            var keepAlive = await Routes.DispatchAsync(request, stream, state, cancellationToken).ConfigureAwait(false);
            if (!keepAlive)
            {
                return;
            }
        }
    }

    private static async Task<Request?> ReadRequestAsync(ConnectionReader reader, CancellationToken cancellationToken)
    {
        var head = await ReadUntilDoubleCrlfAsync(reader, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        var lines = Encoding.UTF8.GetString(head).Split("\r\n");
        var parts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var method = parts.Length > 0 ? parts[0] : "GET";
        var target = parts.Length > 1 ? parts[1] : "/";
        var question = target.IndexOf('?');
        var (path, query) = question >= 0 ? (target[..question], target[(question + 1)..]) : (target, "");

        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (line.Length == 0 || colon < 0)
            {
                continue;
            }
            headers.Add((line[..colon].Trim().ToLowerInvariant(), line[(colon + 1)..].Trim()));
        }

        var request = new Request { Method = method, Path = path, Query = query, Headers = headers };
        request.Body = await ReadBodyAsync(reader, request, cancellationToken).ConfigureAwait(false);
        return request;
    }

    private static async Task<byte[]?> ReadUntilDoubleCrlfAsync(ConnectionReader reader, CancellationToken cancellationToken)
    {
        var head = new List<byte>();
        while (true)
        {
            var next = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                return head.Count == 0 ? null : head.ToArray();
            }
            head.Add((byte)next);
            var count = head.Count;
            if (count >= 4 && head[count - 4] == '\r' && head[count - 3] == '\n' && head[count - 2] == '\r' && head[count - 1] == '\n')
            {
                head.RemoveRange(count - 4, 4);
                return head.ToArray();
            }
            if (count > 64 * 1024)
            {
                return head.ToArray();
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(ConnectionReader reader, Request request, CancellationToken cancellationToken)
    {
        if (request.Header("transfer-encoding") is { } encoding
            && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        var length = int.TryParse(request.Header("content-length"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        var body = new byte[length];
        if (length > 0)
        {
            await reader.ReadExactAsync(body, cancellationToken).ConfigureAwait(false);
        }
        return body;
    }

    private static async Task<byte[]> ReadChunkedAsync(ConnectionReader reader, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
            var sizeText = line.Split(';')[0].Trim();
            var size = int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
            if (size == 0)
            {
                // Consume trailers up to the terminating blank line.
                while ((await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false)).Length > 0)
                {
                }
                break;
            }
            var chunk = new byte[size];
            await reader.ReadExactAsync(chunk, cancellationToken).ConfigureAwait(false);
            body.Write(chunk);
            await reader.ReadExactAsync(new byte[2], cancellationToken).ConfigureAwait(false);
        }
        return body.ToArray();
    }

    private static async Task<string> ReadLineAsync(ConnectionReader reader, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        while (true)
        {
            var next = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0 || next == '\n')
            {
                break;
            }
            if (next != '\r')
            {
                line.Add((byte)next);
            }
        }
        return Encoding.UTF8.GetString(line.ToArray());
    }

    /// <summary>
    /// Writes a response, saying whether the connection stays open. A client
    /// that asked for keep-alive gets it; nothing else changes.
    /// </summary>
    public static async Task WriteResponseAsync(Stream stream, Response response, bool keepAlive, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {response.Reason}\r\n");
        foreach (var (name, value) in response.Headers)
        {
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }
        head.Append(CultureInfo.InvariantCulture, $"content-length: {response.Body.Length}\r\n");
        head.Append(keepAlive ? "connection: keep-alive\r\n\r\n" : "connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Percent-decodes, with <c>+</c> as a space; bytes that are not UTF-8 become replacement characters.</summary>
    public static string Decode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input.Replace('+', ' '));
        var output = new List<byte>(bytes.Length);
        var index = 0;
        while (index < bytes.Length)
        {
            if (bytes[index] == '%' && index + 2 < bytes.Length
                && byte.TryParse(
                    Encoding.ASCII.GetString(bytes, index + 1, 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var decoded))
            {
                output.Add(decoded);
                index += 3;
                continue;
            }
            output.Add(bytes[index]);
            index++;
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }
}
