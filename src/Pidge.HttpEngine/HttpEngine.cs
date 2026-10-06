using Pidge.Core;
using Pidge.Variables;

namespace Pidge.HttpEngine;

/// <summary>
/// Knobs the engine exposes. Everything has a sensible default; the UI does not
/// need to set any of it to send a request. A record, so a changed config is
/// spotted by value (the session rebuilds the engine when it changes).
/// </summary>
public sealed record EngineConfig
{
    /// <summary>30 seconds, as promised in the docs.</summary>
    public const ulong DefaultTimeoutMs = 30_000;

    /// <summary>A response bigger than this is truncated rather than allowed to eat all memory.</summary>
    public const ulong DefaultMaxResponseBytes = 50 * 1024 * 1024;

    /// <summary>Chosen to match what a browser will follow before giving up.</summary>
    public const int DefaultMaxRedirects = 10;

    public static readonly string DefaultUserAgent = "pidge/" + AppVersion.Current;

    public ulong DefaultTimeout { get; init; } = DefaultTimeoutMs;
    public ulong MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public bool FollowRedirects { get; init; } = true;
    public int MaxRedirects { get; init; } = DefaultMaxRedirects;
    public bool StoreCookies { get; init; } = true;
    public string UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>Trust and client-certificate settings.</summary>
    public TlsSettings Tls { get; init; } = new();
}

/// <summary>
/// The HTTP engine. Knows nothing about Photino, VS Code, or any UI. It takes
/// an <see cref="HttpRequest"/>, sends it, and returns a normalized
/// <see cref="HttpResponse"/> or throws a <see cref="RequestErrorException"/>.
///
/// Owns one reusable connection pool. Build it once and share it.
/// </summary>
public sealed partial class HttpEngine : IDisposable
{
    public EngineConfig Config { get; }

    /// <exception cref="RequestErrorException">The TLS settings could not be applied.</exception>
    public HttpEngine(EngineConfig config)
    {
        Config = config;
        Initialize();
    }

    /// <summary>
    /// Sends the request. Variables must already be resolved; use
    /// <see cref="ExecuteWithVariablesAsync"/> if they are not.
    /// Every failure, cancellation and timeout included, surfaces as a
    /// <see cref="RequestErrorException"/>; nothing else escapes.
    /// </summary>
    public Task<HttpResponse> ExecuteAsync(HttpRequest request, CancellationHandle cancellation) =>
        ExecuteCoreAsync(request, cancellation);

    /// <summary>Convenience wrapper: resolve <c>{{variables}}</c> and then send.</summary>
    public Task<HttpResponse> ExecuteWithVariablesAsync(
        HttpRequest request,
        VariableSet variables,
        CancellationHandle cancellation)
    {
        HttpRequest resolved;
        try
        {
            resolved = Substitution.ResolveRequest(request, variables);
        }
        catch (RequestErrorException e)
        {
            return Task.FromException<HttpResponse>(e);
        }
        return ExecuteAsync(resolved, cancellation);
    }

    // Implemented in the engine's other partial files.
    partial void Initialize();

    private partial Task<HttpResponse> ExecuteCoreAsync(HttpRequest request, CancellationHandle cancellation);
}
