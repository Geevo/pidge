namespace Pidge.Core;

/// <summary>What must never reach a log file, and what an export must not give away.</summary>
public static class Redact
{
    private static readonly string[] SecretHeaders =
    [
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "x-auth-token",
        "x-amz-security-token",
        "x-csrf-token",
    ];

    public static bool IsSecretHeader(string name) =>
        SecretHeaders.Contains(name.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Returns the value, or a placeholder if the header is a secret one.</summary>
    public static string HeaderValueForLog(string name, string value) =>
        IsSecretHeader(name) ? "<redacted>" : value;

    /// <summary>
    /// A secret field inside an <see cref="AuthConfig"/>: its wire name, and a
    /// way to read and replace it.
    /// </summary>
    public sealed record AuthSecret(string Field, Func<string> Get, Action<string> Set);

    /// <summary>
    /// Every secret in <paramref name="auth"/>, by the name of its field, for
    /// whatever has to hide them: the state file encrypts them, an export can
    /// replace them. Exhaustive on purpose: a new kind of auth has to decide
    /// what in it is secret. Identifiers — usernames, client ids, OAuth 1
    /// tokens — are not.
    /// </summary>
    public static List<AuthSecret> AuthSecrets(AuthConfig auth) => auth switch
    {
        NoAuth => [],
        BearerAuth a => [new("token", () => a.Token, v => a.Token = v)],
        BasicAuth a => [new("password", () => a.Password, v => a.Password = v)],
        DigestAuth a => [new("password", () => a.Password, v => a.Password = v)],
        NtlmAuth a => [new("password", () => a.Password, v => a.Password = v)],
        OAuth1Settings s =>
        [
            new("consumerSecret", () => s.ConsumerSecret, v => s.ConsumerSecret = v),
            new("tokenSecret", () => s.TokenSecret, v => s.TokenSecret = v),
        ],
        OAuth2Settings s =>
        [
            new("clientSecret", () => s.ClientSecret, v => s.ClientSecret = v),
            new("password", () => s.Password, v => s.Password = v),
            new("refreshToken", () => s.RefreshToken, v => s.RefreshToken = v),
        ],
        ApiKeyAuth a => [new("value", () => a.Value, v => a.Value = v)],
        _ => throw new ArgumentOutOfRangeException(nameof(auth), auth.GetType().Name),
    };

    /// <summary>The scheme's name as the state file spells it, e.g. <c>oauth2</c>.</summary>
    public static string AuthKind(AuthConfig auth) => auth.Kind;
}
