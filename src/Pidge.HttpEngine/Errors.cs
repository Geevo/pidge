using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Pidge.Core;

namespace Pidge.HttpEngine;

internal static class Errors
{
    /// <summary>
    /// Maps a transport failure onto one of our normalized kinds.
    ///
    /// The handler exposes coarse categories, so for the cases developers
    /// actually hit most (refused connections, TLS problems) we also look at
    /// the exception chain.
    /// </summary>
    public static RequestError FromHttp(Exception err, WebUrl? url, bool isBody)
    {
        var detail = Chain(err);
        var lowered = detail.ToLowerInvariant();

        RequestErrorKind kind;
        if (IsDns(err) || lowered.Contains("dns error") || lowered.Contains("name or service not known")
            || lowered.Contains("no such host is known"))
        {
            kind = RequestErrorKind.Dns;
        }
        else if (HasSocketError(err, SocketError.ConnectionRefused) || lowered.Contains("connection refused"))
        {
            kind = RequestErrorKind.ConnectionRefused;
        }
        else if (IsTls(err, lowered))
        {
            kind = RequestErrorKind.Tls;
        }
        else if (IsConnect(err))
        {
            kind = RequestErrorKind.ConnectionFailed;
        }
        else if (isBody)
        {
            kind = RequestErrorKind.BodyRead;
        }
        else
        {
            kind = RequestErrorKind.Other;
        }

        var host = url?.Host ?? "the server";

        var message = kind switch
        {
            RequestErrorKind.Dns => $"Could not resolve `{host}`.",
            RequestErrorKind.ConnectionRefused => $"`{host}` refused the connection. Is the server running?",
            RequestErrorKind.ConnectionFailed => $"Could not connect to `{host}`.",
            RequestErrorKind.Tls => $"TLS handshake with `{host}` failed.",
            RequestErrorKind.BodyRead => "The response body could not be read.",
            _ => $"The request to `{host}` failed.",
        };

        return new RequestError(kind, message).WithDetail(detail);
    }

    private static bool IsDns(Exception err) =>
        Causes(err).Any(e =>
            e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            || e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain });

    private static bool HasSocketError(Exception err, SocketError code) =>
        Causes(err).Any(e => e is SocketException socket && socket.SocketErrorCode == code);

    private static bool IsTls(Exception err, string lowered) =>
        Causes(err).Any(e =>
            e is AuthenticationException
            || e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
        || new[] { "tls", "certificate", "handshake", "ssl", "self-signed" }.Any(lowered.Contains);

    private static bool IsConnect(Exception err) =>
        Causes(err).Any(e =>
            e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }
            || e is SocketException);

    private static IEnumerable<Exception> Causes(Exception err)
    {
        for (Exception? current = err; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>Flattens an exception and its causes into one diagnostics line.</summary>
    public static string Chain(Exception err)
    {
        var parts = new List<string>();
        foreach (var cause in Causes(err))
        {
            var text = cause.Message;
            if (!parts.Contains(text))
            {
                parts.Add(text);
            }
        }
        return string.Join(": ", parts);
    }
}
