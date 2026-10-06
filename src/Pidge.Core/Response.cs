namespace Pidge.Core;

/// <summary>
/// The result of one successful round trip. "Successful" means we got a
/// response, not that the status code was 2xx.
/// </summary>
public sealed class HttpResponse
{
    public ushort Status { get; set; }
    public string StatusText { get; set; } = "";
    public List<KeyValueEntry> Headers { get; set; } = [];

    /// <summary>Raw bytes, base64 encoded on the wire.</summary>
    public byte[] Body { get; set; } = [];

    public string? MimeType { get; set; }
    public ulong DurationMs { get; set; }
    public ulong SizeBytes { get; set; }

    /// <summary>True when the body hit the configured size limit and was cut short.</summary>
    public bool Truncated { get; set; }

    /// <summary>The URL actually reached, after any redirects.</summary>
    public string FinalUrl { get; set; } = "";

    /// <summary>Non-fatal notes for the user, e.g. an auth helper that was overridden.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>The connection's TLS, for the padlock. Null for plain HTTP.</summary>
    public TlsDetails? Tls { get; set; }

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
