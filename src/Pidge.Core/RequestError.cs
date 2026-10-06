using System.Text.Json.Serialization;

namespace Pidge.Core;

/// <summary>
/// Normalized failure categories. The UI switches on these; <c>detail</c> carries
/// the technical chain for diagnostics without dumping it into the main view.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RequestErrorKind>))]
public enum RequestErrorKind
{
    [JsonStringEnumMemberName("invalidUrl")] InvalidUrl,
    [JsonStringEnumMemberName("unsupportedScheme")] UnsupportedScheme,
    [JsonStringEnumMemberName("unresolvedVariable")] UnresolvedVariable,
    [JsonStringEnumMemberName("invalidHeader")] InvalidHeader,
    [JsonStringEnumMemberName("dns")] Dns,
    [JsonStringEnumMemberName("connectionRefused")] ConnectionRefused,
    [JsonStringEnumMemberName("connectionFailed")] ConnectionFailed,
    [JsonStringEnumMemberName("tls")] Tls,
    [JsonStringEnumMemberName("timeout")] Timeout,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("tooManyRedirects")] TooManyRedirects,
    [JsonStringEnumMemberName("redirect")] Redirect,
    /// <summary>
    /// The auth helper could not get credentials, e.g. a token endpoint that
    /// refused. The request itself was never sent.
    /// </summary>
    [JsonStringEnumMemberName("auth")] Auth,
    [JsonStringEnumMemberName("bodySerialization")] BodySerialization,
    [JsonStringEnumMemberName("bodyRead")] BodyRead,
    [JsonStringEnumMemberName("responseTooLarge")] ResponseTooLarge,
    [JsonStringEnumMemberName("io")] Io,
    [JsonStringEnumMemberName("other")] Other,
}

public static class RequestErrorKindExtensions
{
    /// <summary>Short label for the response pane.</summary>
    public static string Title(this RequestErrorKind kind) => kind switch
    {
        RequestErrorKind.InvalidUrl => "Invalid URL",
        RequestErrorKind.UnsupportedScheme => "Unsupported scheme",
        RequestErrorKind.UnresolvedVariable => "Unresolved variable",
        RequestErrorKind.InvalidHeader => "Invalid header",
        RequestErrorKind.Auth => "Authentication failed",
        RequestErrorKind.Dns => "DNS lookup failed",
        RequestErrorKind.ConnectionRefused => "Connection refused",
        RequestErrorKind.ConnectionFailed => "Connection failed",
        RequestErrorKind.Tls => "TLS error",
        RequestErrorKind.Timeout => "Timed out",
        RequestErrorKind.Cancelled => "Cancelled",
        RequestErrorKind.TooManyRedirects => "Too many redirects",
        RequestErrorKind.Redirect => "Redirect error",
        RequestErrorKind.BodySerialization => "Could not build request body",
        RequestErrorKind.BodyRead => "Could not read response body",
        RequestErrorKind.ResponseTooLarge => "Response too large",
        RequestErrorKind.Io => "I/O error",
        _ => "Request failed",
    };
}

/// <summary>A request failure in a shape both frontends can render directly.</summary>
public sealed class RequestError
{
    public RequestError() { }

    public RequestError(RequestErrorKind kind, string message, string? detail = null)
    {
        Kind = kind;
        Message = message;
        Detail = detail;
    }

    public RequestErrorKind Kind { get; set; }

    /// <summary>Plain-language explanation, safe to show in the response pane.</summary>
    public string Message { get; set; } = "";

    /// <summary>Technical chain for the diagnostics disclosure. May contain library detail.</summary>
    public string? Detail { get; set; }

    public RequestError WithDetail(string detail)
    {
        Detail = detail;
        return this;
    }

    public static RequestError InvalidUrl(string message) => new(RequestErrorKind.InvalidUrl, message);

    public static RequestError Cancelled() => new(RequestErrorKind.Cancelled, "Request cancelled.");

    public static RequestError Timeout(ulong timeoutMs) =>
        new(RequestErrorKind.Timeout, $"No response after {timeoutMs} ms.");

    public static RequestError Other(string message) => new(RequestErrorKind.Other, message);

    [JsonIgnore]
    public string Title => Kind.Title();

    public override string ToString() => Message;

    /// <summary>Wraps it to be thrown, for code that unwinds on the first failure.</summary>
    public RequestErrorException AsException() => new(this);
}

/// <summary>
/// Carries a <see cref="RequestError"/> up a call stack from helpers too deep
/// to return one, to the code that reports it.
/// </summary>
public sealed class RequestErrorException(RequestError error) : Exception(error.Message)
{
    public RequestError Error { get; } = error;
}
