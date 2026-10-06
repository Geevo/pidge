using System.Text.Json.Serialization;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/*
 * The request, written out as code for somebody else's client.
 *
 * The point is that the snippet and the Send button agree. Both are built on
 * RequestPlanning.Effective, so the URL, the query string, the headers and the
 * content type in the generated code are the ones that would actually go on
 * the wire — not a second guess at them.
 *
 * What a snippet cannot reproduce it says out loud, in a comment, rather than
 * quietly leaving out: a digest challenge is a flag, but an OAuth 1 signature
 * is a program.
 */

/// <summary>What a file of exported saved requests is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExportFormat>))]
public enum ExportFormat
{
    /// <summary>The saved requests exactly as stored. Nothing is lost.</summary>
    [JsonStringEnumMemberName("json")] Json,

    /// <summary>Plain-text blocks that VS Code's REST Client and JetBrains run.</summary>
    [JsonStringEnumMemberName("http")] Http,
}

/// <summary>
/// One way of writing a request out: a language, and the library it uses.
/// The wire names of the first four are what they have always been.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodeTarget>))]
public enum CodeTarget
{
    [JsonStringEnumMemberName("curl")] Curl,
    [JsonStringEnumMemberName("powershell")] PowerShell,
    [JsonStringEnumMemberName("python")] Python,
    [JsonStringEnumMemberName("csharp")] CSharp,
    [JsonStringEnumMemberName("rust-blocking")] RustBlocking,
    [JsonStringEnumMemberName("rust-async")] RustAsync,
    [JsonStringEnumMemberName("node-fetch")] NodeFetch,
    [JsonStringEnumMemberName("node-axios")] NodeAxios,
    [JsonStringEnumMemberName("go")] Go,
    [JsonStringEnumMemberName("java-httpclient")] JavaHttpClient,
    [JsonStringEnumMemberName("java-okhttp")] JavaOkHttp,
    [JsonStringEnumMemberName("php-curl")] PhpCurl,
    [JsonStringEnumMemberName("php-guzzle")] PhpGuzzle,
    [JsonStringEnumMemberName("zig")] Zig,
}

public static class CodeTargets
{
    /// <summary>In the order the picker offers them, grouped by language.</summary>
    public static readonly CodeTarget[] All =
    [
        CodeTarget.Curl,
        CodeTarget.PowerShell,
        CodeTarget.Python,
        CodeTarget.CSharp,
        CodeTarget.RustBlocking,
        CodeTarget.RustAsync,
        CodeTarget.NodeFetch,
        CodeTarget.NodeAxios,
        CodeTarget.Go,
        CodeTarget.JavaHttpClient,
        CodeTarget.JavaOkHttp,
        CodeTarget.PhpCurl,
        CodeTarget.PhpGuzzle,
        CodeTarget.Zig,
    ];

    /// <summary>The language, which is what the picker lists.</summary>
    public static string Language(this CodeTarget target) => target switch
    {
        CodeTarget.Curl => "curl",
        CodeTarget.PowerShell => "PowerShell",
        CodeTarget.Python => "Python",
        CodeTarget.CSharp => "C#",
        CodeTarget.RustBlocking or CodeTarget.RustAsync => "Rust",
        CodeTarget.NodeFetch or CodeTarget.NodeAxios => "Node.js",
        CodeTarget.Go => "Go",
        CodeTarget.JavaHttpClient or CodeTarget.JavaOkHttp => "Java",
        CodeTarget.PhpCurl or CodeTarget.PhpGuzzle => "PHP",
        _ => "Zig",
    };

    /// <summary>
    /// The library underneath, which is what the tabs under the picker offer.
    /// A language with only one way of doing this has no tabs and no label.
    /// </summary>
    public static string? Library(this CodeTarget target) => target switch
    {
        CodeTarget.RustBlocking => "blocking",
        CodeTarget.RustAsync => "async",
        CodeTarget.NodeFetch => "fetch",
        CodeTarget.NodeAxios => "axios",
        CodeTarget.JavaHttpClient => "HttpClient",
        CodeTarget.JavaOkHttp => "OkHttp",
        CodeTarget.PhpCurl => "cURL",
        CodeTarget.PhpGuzzle => "Guzzle",
        _ => null,
    };

    /// <summary>What the picker calls it, language and library together.</summary>
    public static string Label(this CodeTarget target) =>
        target.Library() is { } library ? $"{target.Language()} ({library})" : target.Language();
}

/// <summary>
/// The settings that are the app's rather than the request's. Generated code
/// has no app around it to inherit these from, so they have to be written into
/// the snippet.
/// </summary>
public sealed record ClientOptions
{
    /// <summary>The default, used when the request does not override it. 0 is no limit.</summary>
    public ulong TimeoutMs { get; init; }

    public bool FollowRedirects { get; init; }

    /// <summary>Trust and identity, straight from Settings.</summary>
    public TlsSettings Tls { get; init; } = new();

    /// <summary>What the app does when nobody has changed anything.</summary>
    public static ClientOptions Standard() => new()
    {
        TimeoutMs = EngineConfig.DefaultTimeoutMs,
        FollowRedirects = true,
        Tls = new TlsSettings(),
    };
}

/// <summary>Which kind of file a client certificate is in.</summary>
public enum IdentityKind
{
    Pem,
    Pkcs12,
}

public static partial class CodeGenerator
{
    /// <summary>
    /// Writes <paramref name="request"/> as <paramref name="target"/>.
    ///
    /// The only failure is a URL that cannot be parsed, because there is then
    /// no request to describe. Everything else a generator cannot express
    /// becomes a comment in the code it returns.
    /// </summary>
    /// <exception cref="RequestErrorException">The URL cannot be parsed.</exception>
    public static string Generate(HttpRequest request, ClientOptions options, CodeTarget target)
    {
        var effective = RequestPlanning.Effective(request);
        var plan = new Plan(request, effective, options);
        return GenerateFor(plan, target);
    }

    // Implemented with the generators.
    private static partial string GenerateFor(Plan plan, CodeTarget target);
}

/// <summary>
/// Everything a generator is handed: what the user typed, what that works out
/// to, and the client-wide settings around it.
/// </summary>
public sealed class Plan(HttpRequest request, EffectiveRequest effective, ClientOptions options)
{
    public HttpRequest Request { get; } = request;
    public EffectiveRequest Effective { get; } = effective;
    public ClientOptions Options { get; } = options;

    public string Method => Request.Method.AsString();

    /// <summary>
    /// Basic auth, when the Auth tab is what supplies it. Null covers a user
    /// who typed the Authorization header themselves: that one is passed
    /// through as typed.
    /// </summary>
    public (string Username, string Password)? Basic =>
        Effective.Auth is AuthPlan.Header && Request.Auth is BasicAuth basic
            ? (basic.Username, basic.Password)
            : null;

    /// <summary>The headers to write out, less the one <see cref="Basic"/> has taken over.</summary>
    public List<(string Name, string Value)> Headers()
    {
        var taken = Basic is not null;
        return Effective.Headers
            .Where(h => !(taken && Text.AsciiEquals(h.Name, "authorization")))
            .ToList();
    }

    /// <summary>Milliseconds, the request's own override ahead of the app's default.</summary>
    public ulong TimeoutMs => Request.TimeoutMs ?? Options.TimeoutMs;

    /// <summary>The client certificate to present, with the kind of file it is in.</summary>
    public (ClientIdentitySettings Identity, IdentityKind Kind)? Identity
    {
        get
        {
            var identity = Options.Tls.ClientIdentity;
            if (identity is null || identity.Path.Trim().Length == 0)
            {
                return null;
            }
            return (identity, KindOf(identity));
        }
    }

    /// <summary>
    /// The extension is what the settings dialog asks for and is the better
    /// signal; a password settles the rest, because a PKCS#12 bundle is always
    /// encrypted and the PEM the engine accepts never is.
    /// </summary>
    public static IdentityKind KindOf(ClientIdentitySettings identity)
    {
        var path = Text.AsciiLower(identity.Path.Trim());
        if (path.EndsWith(".p12", StringComparison.Ordinal) || path.EndsWith(".pfx", StringComparison.Ordinal))
        {
            return IdentityKind.Pkcs12;
        }
        return identity.Password is not null ? IdentityKind.Pkcs12 : IdentityKind.Pem;
    }

    /// <summary>The extra CA files to trust. Blank rows are skipped, as the engine skips them.</summary>
    public List<string> ExtraCaFiles() =>
        Options.Tls.ExtraCaFiles.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>
    /// True when those CAs are added to the system store rather than replacing
    /// it — which none of these clients can do, and all of them have to be told.
    /// </summary>
    public bool MergesCaFiles => Options.Tls.UseSystemRoots;

    public bool AcceptsInvalidCerts => Options.Tls.AcceptInvalidCerts;

    /// <summary>What this snippet cannot do, in plain sentences for each generator to comment in its own way.</summary>
    public List<string> Notes()
    {
        var notes = new List<string>();
        switch (Effective.Auth)
        {
            case AuthPlan.OAuth1:
                notes.Add(
                    "OAuth 1.0a signs every request over its URL and body. This snippet does not "
                    + "sign anything; the app does it at send time.");
                break;
            case AuthPlan.OAuth2 { TokenUrl: var tokenUrl }:
                var endpoint = tokenUrl.Trim().Length == 0 ? "the token endpoint" : tokenUrl;
                notes.Add(
                    $"OAuth 2: the app fetches a token from {endpoint} before it sends. Get one "
                    + "the same way and send it as an Authorization: Bearer header.");
                break;
        }
        notes.AddRange(Effective.Warnings);
        return notes;
    }
}

/// <summary>One request read from a <c>.http</c> file.</summary>
public sealed record ParsedRequest(string Name, HttpRequest Request);

public sealed class ParsedHttpFile
{
    public List<ParsedRequest> Requests { get; } = [];

    /// <summary>One sentence for each request that was left out, saying why.</summary>
    public List<string> Skipped { get; } = [];
}
