using System.Diagnostics;
using System.Text.Json;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// The OAuth 2 grants that are a request to a token endpoint.
///
/// Client credentials, password and refresh token all come down to the same
/// thing: POST a form, read an access token, send it as a bearer token. The
/// interactive flows are not here — those need a browser and a redirect, which
/// is a different kind of program from this one.
///
/// Tokens are cached until they expire, so a burst of requests to the same API
/// costs one token request rather than one each.
/// </summary>
internal static class OAuth2
{
    /// <summary>What a token endpoint answered: the status and the body as text.</summary>
    internal readonly record struct TokenReply(int Status, string Body);

    /// <summary>Fetches an access token, or returns the cached one.</summary>
    /// <param name="send">
    /// Sends the token request. Throws <see cref="RequestErrorException"/> when
    /// nothing came back; a body that could not be read is an empty one.
    /// </param>
    /// <exception cref="RequestErrorException">No token could be had.</exception>
    public static async Task<string> AccessTokenAsync(
        OAuth2Settings settings,
        TokenCache cache,
        Func<string, List<(string Name, string Value)>, string, CancellationToken, Task<TokenReply>> send,
        CancellationToken cancellationToken)
    {
        if (settings.TokenUrl.Trim().Length == 0)
        {
            throw Failed("No token URL is set for OAuth 2.").AsException();
        }

        var key = CacheKey(settings);
        if (cache.Get(key) is { } cached)
        {
            return cached;
        }

        var form = new List<(string Name, string Value)> { ("grant_type", settings.Grant.AsString()) };
        switch (settings.Grant)
        {
            case OAuth2Grant.ClientCredentials:
                break;
            case OAuth2Grant.Password:
                form.Add(("username", settings.Username));
                form.Add(("password", settings.Password));
                break;
            case OAuth2Grant.RefreshToken:
                if (settings.RefreshToken.Trim().Length == 0)
                {
                    throw Failed("No refresh token is set.").AsException();
                }
                form.Add(("refresh_token", settings.RefreshToken.Trim()));
                break;
        }
        if (settings.Scope.Trim().Length > 0)
        {
            form.Add(("scope", settings.Scope.Trim()));
        }

        var headers = new List<(string Name, string Value)> { ("Accept", "application/json") };
        switch (settings.ClientAuth)
        {
            case OAuth2ClientAuth.BasicHeader:
                headers.Add((RequestPlanning.Authorization,
                    RequestPlanning.BasicCredentials(settings.ClientId, settings.ClientSecret)));
                break;
            case OAuth2ClientAuth.RequestBody:
                form.Add(("client_id", settings.ClientId));
                form.Add(("client_secret", settings.ClientSecret));
                break;
        }
        headers.Add(("Content-Type", "application/x-www-form-urlencoded"));

        TokenReply reply;
        try
        {
            reply = await send(settings.TokenUrl.Trim(), headers, PercentEncoding.FormSerialize(form), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestErrorException err)
        {
            throw Failed("The token request could not be sent.")
                .WithDetail(err.Error.Detail ?? err.Error.Message)
                .AsException();
        }

        var body = reply.Body;
        var parsed = TokenResponse.Parse(body);

        // A failed token request is reported in its own terms: the endpoint's
        // `error` field says more than the status code does.
        if (parsed?.Error is { } error)
        {
            throw Failed($"The token endpoint refused the request: {error}.")
                .WithDetail(parsed.ErrorDescription ?? body)
                .AsException();
        }

        if (reply.Status is < 200 or > 299)
        {
            throw Failed($"The token endpoint answered {reply.Status}.")
                .WithDetail(FirstLine(body))
                .AsException();
        }

        if (parsed?.AccessToken is not { } accessToken || accessToken.Trim().Length == 0)
        {
            throw Failed("The token endpoint returned no access_token.")
                .WithDetail(FirstLine(body))
                .AsException();
        }

        cache.Put(key, new CachedToken(
            accessToken,
            parsed.ExpiresIn is { } seconds ? TokenCache.After(TimeSpan.FromSeconds(Math.Min(seconds, (ulong)int.MaxValue))) : null));

        return accessToken;
    }

    /// <summary>
    /// Everything that decides which token comes back, so changing any of it asks
    /// for a new one rather than reusing a token minted for something else.
    /// </summary>
    internal static string CacheKey(OAuth2Settings settings) =>
        string.Join(
            '\u001f',
            settings.TokenUrl.Trim(),
            settings.Grant.AsString(),
            settings.ClientId,
            settings.Scope.Trim(),
            settings.Username,
            settings.RefreshToken.Trim());

    private static RequestError Failed(string message) => new(RequestErrorKind.Auth, message);

    internal static string FirstLine(string body)
    {
        var newline = body.IndexOf('\n');
        var line = newline >= 0 ? body[..newline] : body;
        if (line.EndsWith('\r'))
        {
            line = line[..^1];
        }
        return TakeChars(line, 300);
    }

    /// <summary>The first <paramref name="count"/> characters, never splitting a surrogate pair.</summary>
    private static string TakeChars(string text, int count)
    {
        var taken = 0;
        var index = 0;
        while (index < text.Length && taken < count)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
            taken++;
        }
        return text[..index];
    }

    /// <summary>What the token endpoint sends back. Only <c>access_token</c> is required of it.</summary>
    private sealed class TokenResponse
    {
        public string? AccessToken { get; private set; }
        public ulong? ExpiresIn { get; private set; }
        public string? Error { get; private set; }
        public string? ErrorDescription { get; private set; }

        /// <summary>
        /// Null unless the body is a JSON object whose known fields have the
        /// right types; anything else it carries is ignored.
        /// </summary>
        public static TokenResponse? Parse(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var result = new TokenResponse();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "access_token":
                        case "error":
                        case "error_description":
                        case "expires_in":
                            if (!seen.Add(property.Name))
                            {
                                return null;
                            }
                            break;
                        default:
                            continue;
                    }

                    var value = property.Value;
                    if (property.Name == "expires_in")
                    {
                        if (value.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }
                        var raw = value.GetRawText();
                        if (value.ValueKind != JsonValueKind.Number
                            || raw.AsSpan().IndexOfAny(".eE") >= 0
                            || !value.TryGetUInt64(out var seconds))
                        {
                            return null;
                        }
                        result.ExpiresIn = seconds;
                        continue;
                    }

                    string? text;
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        text = null;
                    }
                    else if (value.ValueKind == JsonValueKind.String)
                    {
                        text = value.GetString();
                    }
                    else
                    {
                        return null;
                    }

                    switch (property.Name)
                    {
                        case "access_token":
                            result.AccessToken = text;
                            break;
                        case "error":
                            result.Error = text;
                            break;
                        default:
                            result.ErrorDescription = text;
                            break;
                    }
                }
                return result;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>A token in hand.</summary>
/// <param name="ExpiresAt">
/// A <see cref="Stopwatch"/> timestamp. Null when the endpoint did not say, in
/// which case it is used once and fetched again next time rather than kept forever.
/// </param>
internal sealed record CachedToken(string AccessToken, long? ExpiresAt);

/// <summary>Tokens in hand, by the settings that fetched them.</summary>
internal sealed class TokenCache
{
    /*
     * A token is dropped a little before it expires. Sending one that expires in
     * transit costs a confusing 401; half a minute of unused life costs nothing.
     */
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, CachedToken> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>The <see cref="Stopwatch"/> timestamp <paramref name="delay"/> from now.</summary>
    public static long After(TimeSpan delay) =>
        Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);

    public string? Get(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var cached) || cached.ExpiresAt is not { } at)
            {
                return null;
            }
            return at <= After(ExpiryMargin) ? null : cached.AccessToken;
        }
    }

    public void Put(string key, CachedToken token)
    {
        lock (_lock)
        {
            _entries[key] = token;
        }
    }
}
