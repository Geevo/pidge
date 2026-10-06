using System.Text.Json.Serialization;

namespace Pidge.Core;

/// <summary>
/// HTTP verbs the client can send. Named for the request rather than
/// <c>HttpMethod</c> so it never collides with <c>System.Net.Http.HttpMethod</c>;
/// the wire name is unchanged.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RequestMethod>))]
public enum RequestMethod
{
    [JsonStringEnumMemberName("GET")] Get,
    [JsonStringEnumMemberName("POST")] Post,
    [JsonStringEnumMemberName("PUT")] Put,
    [JsonStringEnumMemberName("PATCH")] Patch,
    [JsonStringEnumMemberName("DELETE")] Delete,
    [JsonStringEnumMemberName("HEAD")] Head,
    [JsonStringEnumMemberName("OPTIONS")] Options,
}

public static class RequestMethodExtensions
{
    public static readonly RequestMethod[] All =
    [
        RequestMethod.Get,
        RequestMethod.Post,
        RequestMethod.Put,
        RequestMethod.Patch,
        RequestMethod.Delete,
        RequestMethod.Head,
        RequestMethod.Options,
    ];

    public static string AsString(this RequestMethod method) => method switch
    {
        RequestMethod.Get => "GET",
        RequestMethod.Post => "POST",
        RequestMethod.Put => "PUT",
        RequestMethod.Patch => "PATCH",
        RequestMethod.Delete => "DELETE",
        RequestMethod.Head => "HEAD",
        RequestMethod.Options => "OPTIONS",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    /// <summary>The method for an upper-case name, as <c>.http</c> files and curl write it.</summary>
    public static RequestMethod? Parse(string name) => name switch
    {
        "GET" => RequestMethod.Get,
        "POST" => RequestMethod.Post,
        "PUT" => RequestMethod.Put,
        "PATCH" => RequestMethod.Patch,
        "DELETE" => RequestMethod.Delete,
        "HEAD" => RequestMethod.Head,
        "OPTIONS" => RequestMethod.Options,
        _ => null,
    };
}

/// <summary>One editable row in the params, headers, or form editors.</summary>
public sealed class KeyValueEntry
{
    public KeyValueEntry() { }

    public KeyValueEntry(string name, string value, bool enabled = true)
    {
        Id = Ids.NewId();
        Enabled = enabled;
        Name = name;
        Value = value;
    }

    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public static KeyValueEntry New(string name, string value) => new(name, value);

    public static KeyValueEntry Disabled(string name, string value) => new(name, value, enabled: false);

    /// <summary>A row only counts once it has a name; blank trailing rows are ignored.</summary>
    [JsonIgnore]
    public bool IsActive => Enabled && Name.Trim().Length > 0;

    public KeyValueEntry Clone() => new() { Id = Id, Enabled = Enabled, Name = Name, Value = Value };
}

/// <summary>
/// Auth helper configuration. Anything more exotic can be typed as a header.
/// Tagged with a <c>type</c> property, which the UI and state files read.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoAuth), "none")]
[JsonDerivedType(typeof(BearerAuth), "bearer")]
[JsonDerivedType(typeof(BasicAuth), "basic")]
[JsonDerivedType(typeof(DigestAuth), "digest")]
[JsonDerivedType(typeof(NtlmAuth), "ntlm")]
[JsonDerivedType(typeof(OAuth1Settings), "oauth1")]
[JsonDerivedType(typeof(OAuth2Settings), "oauth2")]
[JsonDerivedType(typeof(ApiKeyAuth), "apiKey")]
public abstract class AuthConfig
{
    public static AuthConfig None => new NoAuth();

    [JsonIgnore]
    public bool IsNone => this is NoAuth;

    /// <summary>The scheme's name as the state file spells it, e.g. <c>oauth2</c>.</summary>
    [JsonIgnore]
    public string Kind => KindName;

    // Protected, so the serializer never sees it.
    protected abstract string KindName { get; }

    public abstract AuthConfig Clone();
}

public sealed class NoAuth : AuthConfig
{
    protected override string KindName => "none";
    public override AuthConfig Clone() => new NoAuth();
}

public sealed class BearerAuth : AuthConfig
{
    public string Token { get; set; } = "";
    protected override string KindName => "bearer";
    public override AuthConfig Clone() => new BearerAuth { Token = Token };
}

public sealed class BasicAuth : AuthConfig
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    protected override string KindName => "basic";
    public override AuthConfig Clone() => new BasicAuth { Username = Username, Password = Password };
}

/// <summary>RFC 7616 challenge-response. Nothing is sent until the server asks.</summary>
public sealed class DigestAuth : AuthConfig
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    protected override string KindName => "digest";
    public override AuthConfig Clone() => new DigestAuth { Username = Username, Password = Password };
}

/// <summary>Windows integrated auth over HTTP: MS-NTHT carrying MS-NLMP. NTLMv2.</summary>
public sealed class NtlmAuth : AuthConfig
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Empty is legitimate: a local account has no domain.</summary>
    public string Domain { get; set; } = "";

    /// <summary>Sent as the client's own name. Empty is legitimate.</summary>
    public string Workstation { get; set; } = "";

    protected override string KindName => "ntlm";

    public override AuthConfig Clone() =>
        new NtlmAuth { Username = Username, Password = Password, Domain = Domain, Workstation = Workstation };
}

[JsonConverter(typeof(JsonStringEnumConverter<OAuth1Signature>))]
public enum OAuth1Signature
{
    [JsonStringEnumMemberName("hmacSha1")] HmacSha1,
    [JsonStringEnumMemberName("hmacSha256")] HmacSha256,
    /// <summary>Sends the secrets as the signature. Only defensible over TLS.</summary>
    [JsonStringEnumMemberName("plaintext")] Plaintext,
}

public static class OAuth1SignatureExtensions
{
    /// <summary>The name the specification gives it, which goes in the signed params.</summary>
    public static string AsString(this OAuth1Signature signature) => signature switch
    {
        OAuth1Signature.HmacSha1 => "HMAC-SHA1",
        OAuth1Signature.HmacSha256 => "HMAC-SHA256",
        _ => "PLAINTEXT",
    };
}

/// <summary>
/// OAuth 1.0a (RFC 5849 request signing). Two pairs: the client's, and
/// optionally the user's. On the wire its fields sit beside <c>"type": "oauth1"</c>.
/// </summary>
public sealed class OAuth1Settings : AuthConfig
{
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";

    /// <summary>The user's token, for two-legged requests. Empty is legitimate.</summary>
    public string Token { get; set; } = "";

    public string TokenSecret { get; set; } = "";
    public OAuth1Signature SignatureMethod { get; set; }

    /// <summary>Sent in the header if set; it is not part of the signature.</summary>
    public string Realm { get; set; } = "";

    protected override string KindName => "oauth1";

    public override AuthConfig Clone() => CloneSettings();

    public OAuth1Settings CloneSettings() => new()
    {
        ConsumerKey = ConsumerKey,
        ConsumerSecret = ConsumerSecret,
        Token = Token,
        TokenSecret = TokenSecret,
        SignatureMethod = SignatureMethod,
        Realm = Realm,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<OAuth2Grant>))]
public enum OAuth2Grant
{
    [JsonStringEnumMemberName("clientCredentials")] ClientCredentials,
    [JsonStringEnumMemberName("password")] Password,
    [JsonStringEnumMemberName("refreshToken")] RefreshToken,
}

public static class OAuth2GrantExtensions
{
    public static string AsString(this OAuth2Grant grant) => grant switch
    {
        OAuth2Grant.ClientCredentials => "client_credentials",
        OAuth2Grant.Password => "password",
        _ => "refresh_token",
    };
}

/// <summary>
/// How the client identifies itself to the token endpoint. The specification
/// prefers the header and allows the body; servers differ on which they accept.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuth2ClientAuth>))]
public enum OAuth2ClientAuth
{
    [JsonStringEnumMemberName("basicHeader")] BasicHeader,
    [JsonStringEnumMemberName("requestBody")] RequestBody,
}

/// <summary>
/// The machine-to-machine half of OAuth 2: the grants that are a request to a
/// token endpoint. On the wire its fields sit beside <c>"type": "oauth2"</c>.
/// </summary>
public sealed class OAuth2Settings : AuthConfig
{
    public OAuth2Grant Grant { get; set; }
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Space separated, as the specification has it.</summary>
    public string Scope { get; set; } = "";

    /// <summary>The password grant only.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>The refresh token grant only.</summary>
    public string RefreshToken { get; set; } = "";

    public OAuth2ClientAuth ClientAuth { get; set; }

    protected override string KindName => "oauth2";

    public override AuthConfig Clone() => CloneSettings();

    public OAuth2Settings CloneSettings() => new()
    {
        Grant = Grant,
        TokenUrl = TokenUrl,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Scope = Scope,
        Username = Username,
        Password = Password,
        RefreshToken = RefreshToken,
        ClientAuth = ClientAuth,
    };
}

/// <summary>Where an API key goes. A header by default: a query string ends up in logs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApiKeyPlacement>))]
public enum ApiKeyPlacement
{
    [JsonStringEnumMemberName("header")] Header,
    [JsonStringEnumMemberName("query")] Query,
}

/// <summary>A key in a header or the query string, which is most "API key" auth.</summary>
public sealed class ApiKeyAuth : AuthConfig
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public ApiKeyPlacement Placement { get; set; }
    protected override string KindName => "apiKey";
    public override AuthConfig Clone() => new ApiKeyAuth { Key = Key, Value = Value, Placement = Placement };
}

/// <summary>A single part of a multipart/form-data body.</summary>
public sealed class MultipartEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public MultipartValue Value { get; set; } = new MultipartText();

    public static MultipartEntry Text(string name, string value) => new()
    {
        Id = Ids.NewId(),
        Enabled = true,
        Name = name,
        Value = new MultipartText { Value = value },
    };

    public static MultipartEntry File(string name, string path) => new()
    {
        Id = Ids.NewId(),
        Enabled = true,
        Name = name,
        Value = new MultipartFile { Path = path },
    };

    [JsonIgnore]
    public bool IsActive => Enabled && Name.Trim().Length > 0;

    public MultipartEntry Clone() => new() { Id = Id, Enabled = Enabled, Name = Name, Value = Value.Clone() };
}

/// <summary>Internally tagged with <c>kind</c>, not <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MultipartText), "text")]
[JsonDerivedType(typeof(MultipartFile), "file")]
public abstract class MultipartValue
{
    public abstract MultipartValue Clone();
}

public sealed class MultipartText : MultipartValue
{
    public string Value { get; set; } = "";
    public override MultipartValue Clone() => new MultipartText { Value = Value };
}

public sealed class MultipartFile : MultipartValue
{
    public string Path { get; set; } = "";
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public override MultipartValue Clone() =>
        new MultipartFile { Path = Path, FileName = FileName, ContentType = ContentType };
}

/// <summary>
/// Request body. <see cref="TextBody"/> carries its own content type so the user
/// can send XML, CSV, or anything else without reaching for the headers tab.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoBody), "none")]
[JsonDerivedType(typeof(JsonBody), "json")]
[JsonDerivedType(typeof(TextBody), "text")]
[JsonDerivedType(typeof(UrlEncodedBody), "urlEncoded")]
[JsonDerivedType(typeof(MultipartBody), "multipart")]
public abstract class RequestBody
{
    public static RequestBody None => new NoBody();

    [JsonIgnore]
    public bool IsEmpty => Empty;

    // Protected, so the serializer never sees it.
    protected abstract bool Empty { get; }

    public abstract RequestBody Clone();
}

public sealed class NoBody : RequestBody
{
    protected override bool Empty => true;
    public override RequestBody Clone() => new NoBody();
}

public sealed class JsonBody : RequestBody
{
    public string Text { get; set; } = "";
    protected override bool Empty => Text.Trim().Length == 0;
    public override RequestBody Clone() => new JsonBody { Text = Text };
}

public sealed class TextBody : RequestBody
{
    public string Text { get; set; } = "";
    public string? ContentType { get; set; }
    protected override bool Empty => Text.Trim().Length == 0;
    public override RequestBody Clone() => new TextBody { Text = Text, ContentType = ContentType };
}

public sealed class UrlEncodedBody : RequestBody
{
    public List<KeyValueEntry> Entries { get; set; } = [];
    protected override bool Empty => !Entries.Any(e => e.IsActive);
    public override RequestBody Clone() => new UrlEncodedBody { Entries = Entries.Select(e => e.Clone()).ToList() };
}

public sealed class MultipartBody : RequestBody
{
    public List<MultipartEntry> Entries { get; set; } = [];
    protected override bool Empty => !Entries.Any(e => e.IsActive);
    public override RequestBody Clone() => new MultipartBody { Entries = Entries.Select(e => e.Clone()).ToList() };
}

/// <summary>
/// Everything needed to send one request. This is the unit the UI edits,
/// the engine executes, and storage persists.
/// </summary>
public sealed class HttpRequest
{
    public string Id { get; set; } = "";
    public RequestMethod Method { get; set; }
    public string Url { get; set; } = "";
    public List<KeyValueEntry> QueryParams { get; set; } = [];
    public List<KeyValueEntry> Headers { get; set; } = [];
    public AuthConfig Auth { get; set; } = new NoAuth();
    public RequestBody Body { get; set; } = new NoBody();

    /// <summary>Overrides the default timeout for this request only.</summary>
    public ulong? TimeoutMs { get; set; }

    /// <summary>
    /// Whether query parameters are percent-encoded on their way into the URL.
    /// On is right for almost everything. Off is for a value that is already
    /// encoded, or that holds a <c>/</c> or <c>:</c> a server wants to see
    /// unescaped. Defaulted, so a request saved before this existed still loads.
    /// </summary>
    public bool EncodeQuery { get; set; } = true;

    /// <summary>The state the app opens into: a blank GET with nothing filled in.</summary>
    public static HttpRequest Blank() => new() { Id = Ids.NewId() };

    public static HttpRequest Get(string url) => new() { Id = Ids.NewId(), Url = url };

    /// <summary>True when the request has nothing a user would miss.</summary>
    [JsonIgnore]
    public bool IsUntouched =>
        Method == RequestMethod.Get
        && Url.Trim().Length == 0
        && !QueryParams.Any(e => e.IsActive)
        && !Headers.Any(e => e.IsActive)
        && Auth.IsNone
        && Body.IsEmpty;

    /// <summary>Case-insensitive lookup across enabled header rows.</summary>
    public KeyValueEntry? FindHeader(string name) =>
        Headers.Where(h => h.IsActive)
            .FirstOrDefault(h => string.Equals(h.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    public HttpRequest Clone() => new()
    {
        Id = Id,
        Method = Method,
        Url = Url,
        QueryParams = QueryParams.Select(e => e.Clone()).ToList(),
        Headers = Headers.Select(e => e.Clone()).ToList(),
        Auth = Auth.Clone(),
        Body = Body.Clone(),
        TimeoutMs = TimeoutMs,
        EncodeQuery = EncodeQuery,
    };
}
