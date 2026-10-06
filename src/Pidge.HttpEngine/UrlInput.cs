using Pidge.Core;

namespace Pidge.HttpEngine;

public static class UrlInput
{
    /// <summary>
    /// Turns what the user typed into a URL we can send.
    ///
    /// <c>localhost:3000/test</c> is a hostname and port to a developer, but a
    /// scheme and path to a URL parser, so bare inputs get <c>http://</c> and
    /// nothing else.
    /// </summary>
    /// <exception cref="RequestErrorException">The input is not a URL that can be sent.</exception>
    public static WebUrl NormalizeUrl(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            throw RequestError.InvalidUrl("Enter a URL.").AsException();
        }

        string candidate;
        if (HasSupportedScheme(trimmed))
        {
            candidate = trimmed;
        }
        else if (SplitScheme(trimmed) is { } scheme)
        {
            throw Unsupported(scheme);
        }
        else
        {
            candidate = "http://" + trimmed;
        }

        // The parser knows the other special schemes too; they are refused here.
        if (!WebUrl.TryParse(candidate, out var url, out var error))
        {
            throw RequestError.InvalidUrl($"`{trimmed}` is not a valid URL.").WithDetail(error).AsException();
        }

        if (url.Scheme is not ("http" or "https"))
        {
            throw Unsupported(url.Scheme);
        }

        if (url.Host.Length == 0)
        {
            throw RequestError.InvalidUrl($"`{trimmed}` has no host.").AsException();
        }

        return url;
    }

    public static bool TryNormalizeUrl(string input, out WebUrl url, out RequestError error)
    {
        try
        {
            url = NormalizeUrl(input);
            error = null!;
            return true;
        }
        catch (RequestErrorException e)
        {
            url = null!;
            error = e.Error;
            return false;
        }
    }

    private static RequestErrorException Unsupported(string scheme) =>
        new RequestError(
            RequestErrorKind.UnsupportedScheme,
            $"{scheme}:// is not supported. Use http:// or https://.").AsException();

    private static bool HasSupportedScheme(string input) =>
        input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Detects <c>scheme://rest</c>. A bare <c>localhost:3000</c> is deliberately not a match.</summary>
    private static string? SplitScheme(string input)
    {
        var index = input.IndexOf("://", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }
        var scheme = input[..index];
        var valid = scheme.Length > 0
            && char.IsAsciiLetter(scheme[0])
            && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
        return valid ? scheme : null;
    }
}
