using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pidge.Core;
using Pidge.Variables;
using LocalServer = Pidge.TestServer.TestServer;

namespace Pidge.HttpEngine.Tests;

/// <summary>Engine tests against a local server. Nothing here touches the internet.</summary>
public class RequestTests
{
    private static HttpEngine Engine() => new(new EngineConfig());

    private static HttpEngine Engine(EngineConfig config) => new(config);

    private static JsonElement BodyJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    private static async Task<HttpResponse> SendAsync(HttpRequest request, EngineConfig? config = null)
    {
        using var engine = Engine(config ?? new EngineConfig());
        return await engine.ExecuteAsync(request, new CancellationHandle());
    }

    private static async Task<RequestError> FailsAsync(Task<HttpResponse> sending) =>
        (await Assert.ThrowsAsync<RequestErrorException>(() => sending)).Error;

    private static Task<RequestError> FailsAsync(HttpRequest request, EngineConfig? config = null) =>
        FailsAsync(SendAsync(request, config));

    [Fact]
    public async Task SendsAPlainGet()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(HttpRequest.Get(server.Url("/json")));

        Assert.Equal(200, response.Status);
        Assert.Equal("OK", response.StatusText);
        Assert.Equal("application/json", response.MimeType);
        Assert.True(BodyJson(response.Body).GetProperty("ok").GetBoolean());
        Assert.Equal((ulong)response.Body.Length, response.SizeBytes);
        Assert.False(response.Truncated);
    }

    [Fact]
    public void BareHostAndPortBecomesHttp() =>
        Assert.Equal("http://localhost:3000/test", UrlInput.NormalizeUrl("localhost:3000/test").ToString());

    [Fact]
    public void RejectsUnsupportedSchemesAndEmptyInput()
    {
        Assert.Equal(
            RequestErrorKind.UnsupportedScheme,
            Assert.Throws<RequestErrorException>(() => UrlInput.NormalizeUrl("ftp://example.com")).Error.Kind);
        Assert.Equal(
            RequestErrorKind.InvalidUrl,
            Assert.Throws<RequestErrorException>(() => UrlInput.NormalizeUrl("   ")).Error.Kind);
        Assert.Equal(
            RequestErrorKind.InvalidUrl,
            Assert.Throws<RequestErrorException>(() => UrlInput.NormalizeUrl("http://")).Error.Kind);
    }

    [Fact]
    public async Task AParamInBothTheUrlAndTheTableArrivesOnce()
    {
        await using var server = await LocalServer.StartAsync();

        // What the UI produces: editing the table writes the URL, so the same pair
        // is in both. The server should see it once.
        var request = HttpRequest.Get(server.Url("/echo?postcode=SW1A%201AA"));
        request.QueryParams = [KeyValueEntry.New("postcode", "SW1A 1AA")];

        var body = BodyJson((await SendAsync(request)).Body);

        Assert.Equal("postcode=SW1A%201AA", Text(body, "rawQuery"));
        Assert.Equal("SW1A 1AA", Text(body.GetProperty("query"), "postcode"));
    }

    [Fact]
    public async Task QueryParamsAreAppendedWithoutDroppingExistingOnes()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo?existing=1"));
        request.QueryParams =
        [
            KeyValueEntry.New("q", "hello world"),
            KeyValueEntry.Disabled("skipped", "nope"),
            KeyValueEntry.New("", "no name is not a param"),
        ];

        var query = BodyJson((await SendAsync(request)).Body).GetProperty("query");

        Assert.Equal("1", Text(query, "existing"));
        Assert.Equal("hello world", Text(query, "q"));
        Assert.False(query.TryGetProperty("skipped", out _));
    }

    [Fact]
    public async Task SendsEnabledHeadersOnly()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/headers"));
        request.Headers = [KeyValueEntry.New("X-Custom", "yes"), KeyValueEntry.Disabled("X-Skipped", "no")];

        var text = Encoding.UTF8.GetString((await SendAsync(request)).Body).ToLowerInvariant();

        Assert.Contains("x-custom", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x-skipped", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsInvalidHeaderNames()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/json"));
        request.Headers = [KeyValueEntry.New("bad header", "value")];

        Assert.Equal(RequestErrorKind.InvalidHeader, (await FailsAsync(request)).Kind);
    }

    [Fact]
    public async Task PostsAJsonBodyWithAContentType()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo"));
        request.Method = RequestMethod.Post;
        request.Body = new JsonBody { Text = """{"name":"ada"}""" };

        var echoed = BodyJson((await SendAsync(request)).Body);

        Assert.Equal("POST", Text(echoed, "method"));
        Assert.Equal("application/json", Text(echoed, "contentType"));
        Assert.Equal("""{"name":"ada"}""", Text(echoed, "body"));
    }

    [Fact]
    public async Task TheQueryGoesOutAsTheUrlShowsIt()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo") + "?a=%41%7e&b=x|y`z&c=%zz#top");

        var echoed = BodyJson((await SendAsync(request)).Body);

        Assert.Equal("a=%41%7e&b=x|y`z&c=%zz", Text(echoed, "rawQuery"));
    }

    [Fact]
    public async Task AnInvalidJsonBodyIsRefusedBeforeSending()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo"));
        request.Method = RequestMethod.Post;
        request.Body = new JsonBody { Text = "{\n  \"name\": \"ada\",\n}" };

        var error = await FailsAsync(request);

        Assert.Equal(RequestErrorKind.BodySerialization, error.Kind);
        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task ABlankJsonBodyIsStillSent()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo"));
        request.Method = RequestMethod.Post;
        request.Body = new JsonBody { Text = "  \n" };

        Assert.Equal(200, (await SendAsync(request)).Status);
    }

    [Fact]
    public async Task AnExplicitContentTypeIsNotOverwritten()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo"));
        request.Method = RequestMethod.Post;
        request.Headers = [KeyValueEntry.New("Content-Type", "application/vnd.api+json")];
        request.Body = new JsonBody { Text = "{}" };

        Assert.Equal("application/vnd.api+json", Text(BodyJson((await SendAsync(request)).Body), "contentType"));
    }

    [Fact]
    public async Task SendsUrlEncodedBodies()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo"));
        request.Method = RequestMethod.Post;
        request.Body = new UrlEncodedBody
        {
            Entries = [KeyValueEntry.New("name", "ada lovelace"), KeyValueEntry.Disabled("ignored", "yes")],
        };

        var echoed = BodyJson((await SendAsync(request)).Body);

        Assert.Equal("application/x-www-form-urlencoded", Text(echoed, "contentType"));
        Assert.Equal("name=ada+lovelace", Text(echoed, "body"));
    }

    [Fact]
    public async Task SendsMultipartBodies()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/multipart"));
        request.Method = RequestMethod.Post;
        request.Body = new MultipartBody { Entries = [MultipartEntry.Text("field", "value")] };

        var echoed = BodyJson((await SendAsync(request)).Body);

        Assert.StartsWith("multipart/form-data; boundary=", Text(echoed, "contentType"), StringComparison.Ordinal);
        var raw = Text(echoed, "body")!;
        Assert.Contains("name=\"field\"", raw, StringComparison.Ordinal);
        Assert.Contains("value", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipartSendsFileContents()
    {
        await using var server = await LocalServer.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), $"api-client-test-{Ids.NewId()}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.txt");
        await File.WriteAllBytesAsync(path, "file contents here"u8.ToArray());

        var request = HttpRequest.Get(server.Url("/multipart"));
        request.Method = RequestMethod.Post;
        request.Body = new MultipartBody { Entries = [MultipartEntry.File("upload", path)] };

        string raw;
        try
        {
            raw = Text(BodyJson((await SendAsync(request)).Body), "body")!;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Assert.Contains("filename=\"note.txt\"", raw, StringComparison.Ordinal);
        Assert.Contains("file contents here", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMultipartFilesAreReportedClearly()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/multipart"));
        request.Method = RequestMethod.Post;
        request.Body = new MultipartBody { Entries = [MultipartEntry.File("upload", "/definitely/not/here.txt")] };

        Assert.Equal(RequestErrorKind.Io, (await FailsAsync(request)).Kind);
    }

    [Fact]
    public async Task AppliesBearerAuth()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Auth = new BearerAuth { Token = "secret-token" };

        var response = await SendAsync(request);

        Assert.Equal("Bearer secret-token", Text(BodyJson(response.Body), "authorization"));
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task AppliesBasicAuth()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Auth = new BasicAuth { Username = "ada", Password = "lovelace" };

        // base64("ada:lovelace")
        Assert.Equal("Basic YWRhOmxvdmVsYWNl", Text(BodyJson((await SendAsync(request)).Body), "authorization"));
    }

    [Fact]
    public async Task AnExplicitAuthorizationHeaderWinsAndWarns()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Headers = [KeyValueEntry.New("Authorization", "Token typed-by-hand")];
        request.Auth = new BearerAuth { Token = "from-the-auth-tab" };

        var response = await SendAsync(request);

        Assert.Equal("Token typed-by-hand", Text(BodyJson(response.Body), "authorization"));
        var warning = Assert.Single(response.Warnings);
        Assert.Contains("Authorization", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledAuthorizationHeaderDoesNotBlockTheAuthTab()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Headers = [KeyValueEntry.Disabled("Authorization", "Token ignored")];
        request.Auth = new BearerAuth { Token = "wins" };

        var response = await SendAsync(request);

        Assert.Equal("Bearer wins", Text(BodyJson(response.Body), "authorization"));
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task ReportsNon2xxStatusesAsResponsesNotErrors()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(HttpRequest.Get(server.Url("/status/418")));

        Assert.Equal(418, response.Status);
        Assert.Equal("I'm a teapot", response.StatusText);
    }

    [Fact]
    public async Task FollowsRedirectsToTheFinalUrl()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(HttpRequest.Get(server.Url("/redirect/3")));

        Assert.Equal(200, response.Status);
        Assert.EndsWith("/json", response.FinalUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectLoopIsANormalizedError()
    {
        await using var server = await LocalServer.StartAsync();
        var error = await FailsAsync(HttpRequest.Get(server.Url("/redirect-loop")));

        Assert.Equal(RequestErrorKind.TooManyRedirects, error.Kind);
        Assert.NotNull(error.Detail);
    }

    [Fact]
    public async Task RedirectsCanBeTurnedOff()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(
            HttpRequest.Get(server.Url("/redirect/1")),
            new EngineConfig { FollowRedirects = false });

        Assert.Equal(302, response.Status);
        Assert.Equal("/json", response.Header("location"));
    }

    [Fact]
    public async Task CookiesSetByTheServerComeBackOnTheNextRequest()
    {
        await using var server = await LocalServer.StartAsync();
        using var engine = Engine();

        var first = await engine.ExecuteAsync(HttpRequest.Get(server.Url("/set-cookie")), new CancellationHandle());
        Assert.Equal("session=abc123; Path=/", first.Header("set-cookie"));

        var second = await engine.ExecuteAsync(HttpRequest.Get(server.Url("/cookie")), new CancellationHandle());
        Assert.Equal("session=abc123", Text(BodyJson(second.Body), "cookie"));
    }

    [Fact]
    public async Task BinaryResponsesSurviveIntact()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(HttpRequest.Get(server.Url("/binary")));

        Assert.Equal(new byte[] { 0, 159, 146, 150, 255, 1, 2, 3 }, response.Body);
        Assert.Equal("application/octet-stream", response.MimeType);
    }

    [Fact]
    public async Task InvalidJsonIsReturnedAsBytesNotAnError()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(HttpRequest.Get(server.Url("/invalid-json")));

        Assert.Equal(200, response.Status);
        Assert.Equal("{ this is not json"u8.ToArray(), response.Body);
    }

    [Fact]
    public async Task LargeResponsesAreTruncatedAtTheLimit()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(
            HttpRequest.Get(server.Url("/large/8192")),
            new EngineConfig { MaxResponseBytes = 1024 });

        Assert.True(response.Truncated);
        Assert.Equal(1024UL, response.SizeBytes);
    }

    [Fact]
    public async Task ResponsesUnderTheLimitAreNotTruncated()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(
            HttpRequest.Get(server.Url("/large/4096")),
            new EngineConfig { MaxResponseBytes = 8192 });

        Assert.False(response.Truncated);
        Assert.Equal(4096UL, response.SizeBytes);
    }

    [Fact]
    public async Task TimesOutInsteadOfHanging()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/never"));
        request.TimeoutMs = 150;

        var error = await FailsAsync(request);

        Assert.Equal(RequestErrorKind.Timeout, error.Kind);
        Assert.Contains("150", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimesOutWhileTheBodyIsStillArriving()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/slow-body/20/200"));
        request.TimeoutMs = 250;

        Assert.Equal(RequestErrorKind.Timeout, (await FailsAsync(request)).Kind);
    }

    [Fact]
    public async Task CancellationStopsAPendingRequest()
    {
        await using var server = await LocalServer.StartAsync();
        var handle = new CancellationHandle();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            handle.Cancel();
        });

        using var engine = Engine();
        var error = await FailsAsync(engine.ExecuteAsync(HttpRequest.Get(server.Url("/never")), handle));

        Assert.Equal(RequestErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public async Task CancellationStopsABodyThatIsStillDownloading()
    {
        await using var server = await LocalServer.StartAsync();
        var handle = new CancellationHandle();
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            handle.Cancel();
        });

        using var engine = Engine();
        var error = await FailsAsync(engine.ExecuteAsync(HttpRequest.Get(server.Url("/slow-body/50/50")), handle));

        Assert.Equal(RequestErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public async Task AnAlreadyCancelledHandleNeverSends()
    {
        await using var server = await LocalServer.StartAsync();
        var handle = new CancellationHandle();
        handle.Cancel();

        using var engine = Engine();
        var error = await FailsAsync(engine.ExecuteAsync(HttpRequest.Get(server.Url("/json")), handle));

        Assert.Equal(RequestErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public async Task ARefusedConnectionSaysSo()
    {
        // Bind and drop, so the port is almost certainly closed.
        int port;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }

        var error = await FailsAsync(HttpRequest.Get($"http://127.0.0.1:{port}/"));

        Assert.Equal(RequestErrorKind.ConnectionRefused, error.Kind);
        Assert.Contains("127.0.0.1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvedVariablesFailBeforeAnythingIsSent()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get($"{server.BaseUrl}/{{{{missing}}}}");

        using var engine = Engine();
        var error = await FailsAsync(engine.ExecuteWithVariablesAsync(request, new VariableSet(), new CancellationHandle()));

        Assert.Equal(RequestErrorKind.UnresolvedVariable, error.Kind);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task VariablesAreSubstitutedBeforeSending()
    {
        await using var server = await LocalServer.StartAsync();
        var variables = new VariableSet([("baseUrl", server.BaseUrl), ("token", "abc")]);

        var request = HttpRequest.Get("{{baseUrl}}/auth");
        request.Auth = new BearerAuth { Token = "{{token}}" };

        using var engine = Engine();
        var response = await engine.ExecuteWithVariablesAsync(request, variables, new CancellationHandle());

        Assert.Equal("Bearer abc", Text(BodyJson(response.Body), "authorization"));
    }

    [Fact]
    public async Task HeadRequestsHaveNoBody()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/json"));
        request.Method = RequestMethod.Head;

        var response = await SendAsync(request);

        Assert.Equal(200, response.Status);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task TimingIsRecorded()
    {
        await using var server = await LocalServer.StartAsync();
        var response = await SendAsync(HttpRequest.Get(server.Url("/delay/60")));

        Assert.True(response.DurationMs >= 60, $"got {response.DurationMs}");
    }

    private static List<(string Name, string Value)> SentHeaders(HttpResponse response) =>
        BodyJson(response.Body).GetProperty("headers").EnumerateArray()
            .Select(pair => (pair[0].GetString()!, pair[1].GetString()!))
            .ToList();

    [Fact]
    public async Task PutsAnApiKeyInTheHeaderItNames()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/headers"));
        request.Auth = new ApiKeyAuth { Key = "X-API-Key", Value = "secret-key", Placement = ApiKeyPlacement.Header };

        var response = await SendAsync(request);

        Assert.Contains(("x-api-key", "secret-key"), SentHeaders(response));
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task PutsAnApiKeyInTheQueryStringWhenAsked()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo?existing=kept"));
        request.Auth = new ApiKeyAuth
        {
            Key = "api_key",
            Value = "secret key/with symbols",
            Placement = ApiKeyPlacement.Query,
        };

        var query = BodyJson((await SendAsync(request)).Body).GetProperty("query");

        // Escaped like any other param, and beside what was already there.
        Assert.Equal("kept", Text(query, "existing"));
        Assert.Equal("secret key/with symbols", Text(query, "api_key"));
    }

    [Fact]
    public async Task AnApiKeyHeaderTypedByHandWinsAndWarns()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/headers"));
        request.Headers = [KeyValueEntry.New("X-API-Key", "typed-by-hand")];
        request.Auth = new ApiKeyAuth
        {
            Key = "X-API-Key",
            Value = "from-the-auth-tab",
            Placement = ApiKeyPlacement.Header,
        };

        var response = await SendAsync(request);

        var sent = Assert.Single(SentHeaders(response), pair => pair.Name == "x-api-key");
        Assert.Equal("typed-by-hand", sent.Value);
        var warning = Assert.Single(response.Warnings);
        Assert.Contains("X-API-Key", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiKeyWithNoNameIsIgnored()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/echo"));
        request.Auth = new ApiKeyAuth { Key = "  ", Value = "orphan", Placement = ApiKeyPlacement.Query };

        var query = BodyJson((await SendAsync(request)).Body).GetProperty("query");

        Assert.Empty(query.EnumerateObject());
    }

    [Fact]
    public async Task AnswersADigestChallengeAndRetries()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/digest?page=2"));
        request.Auth = new DigestAuth { Username = "ada", Password = "lovelace" };

        var response = await SendAsync(request);

        // The server recomputes the digest, so 200 means the arithmetic was right.
        Assert.Equal(200, response.Status);
        var body = BodyJson(response.Body);
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        // Digested over the path and query, not the whole URL.
        Assert.Equal("/digest?page=2", Text(body, "uri"));
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task AWrongDigestPasswordLeavesThe401Alone()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/digest"));
        request.Auth = new DigestAuth { Username = "ada", Password = "not-the-password" };

        Assert.Equal(401, (await SendAsync(request)).Status);
    }

    [Fact]
    public async Task AChallengeInAnotherSchemeIsExplainedNotRetried()
    {
        await using var server = await LocalServer.StartAsync();
        // /status/401 answers without a WWW-Authenticate header at all.
        var request = HttpRequest.Get(server.Url("/status/401"));
        request.Auth = new DigestAuth { Username = "ada", Password = "lovelace" };

        var response = await SendAsync(request);

        Assert.Equal(401, response.Status);
        var warning = Assert.Single(response.Warnings);
        Assert.Contains("no challenge", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DigestSendsNothingUntilItIsAsked()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Auth = new DigestAuth { Username = "ada", Password = "lovelace" };

        // A server that does not challenge gets no credentials.
        Assert.Equal("", Text(BodyJson((await SendAsync(request)).Body), "authorization"));
    }

    private static OAuth2Settings OAuth2(LocalServer server, OAuth2Grant grant) => new()
    {
        Grant = grant,
        TokenUrl = server.Url("/oauth/token"),
        ClientId = "test-client",
        ClientSecret = "test-secret",
    };

    [Fact]
    public async Task FetchesATokenWithClientCredentialsAndSendsIt()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/oauth/protected"));
        request.Auth = OAuth2(server, OAuth2Grant.ClientCredentials);

        var response = await SendAsync(request);

        Assert.Equal(200, response.Status);
        Assert.StartsWith("issued-token-", Text(BodyJson(response.Body), "token"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReusesTheTokenAcrossRequests()
    {
        await using var server = await LocalServer.StartAsync();
        using var engine = Engine();
        var settings = OAuth2(server, OAuth2Grant.ClientCredentials);

        var first = HttpRequest.Get(server.Url("/oauth/protected"));
        first.Auth = settings.CloneSettings();
        var second = HttpRequest.Get(server.Url("/oauth/protected"));
        second.Auth = settings;

        var one = await engine.ExecuteAsync(first, new CancellationHandle());
        var two = await engine.ExecuteAsync(second, new CancellationHandle());

        // The same token, so the endpoint was asked once rather than twice.
        Assert.Equal(Text(BodyJson(one.Body), "token"), Text(BodyJson(two.Body), "token"));
    }

    [Fact]
    public async Task SendsTheClientInTheBodyWhenAskedTo()
    {
        await using var server = await LocalServer.StartAsync();
        var settings = OAuth2(server, OAuth2Grant.ClientCredentials);
        settings.ClientAuth = OAuth2ClientAuth.RequestBody;
        // A different scope, so this does not read the previous test's token.
        settings.Scope = "read:things";

        var request = HttpRequest.Get(server.Url("/oauth/protected"));
        request.Auth = settings;

        Assert.Equal(200, (await SendAsync(request)).Status);
    }

    [Fact]
    public async Task ThePasswordGrantSendsTheUsernameAndPassword()
    {
        await using var server = await LocalServer.StartAsync();
        var settings = OAuth2(server, OAuth2Grant.Password);
        settings.Username = "ada";
        settings.Password = "lovelace";

        var request = HttpRequest.Get(server.Url("/oauth/protected"));
        request.Auth = settings;

        Assert.Equal(200, (await SendAsync(request)).Status);
    }

    [Fact]
    public async Task ARefusedTokenRequestReportsWhatTheEndpointSaid()
    {
        await using var server = await LocalServer.StartAsync();
        var settings = OAuth2(server, OAuth2Grant.ClientCredentials);
        settings.ClientSecret = "the-wrong-secret";

        var request = HttpRequest.Get(server.Url("/oauth/protected"));
        request.Auth = settings;

        var error = await FailsAsync(request);

        Assert.Equal(RequestErrorKind.Auth, error.Kind);
        Assert.Contains("invalid_client", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Detail);
    }

    [Fact]
    public async Task OAuth2WithNoTokenUrlSaysSoBeforeSendingAnything()
    {
        var request = HttpRequest.Get("http://127.0.0.1:1/never-reached");
        request.Auth = new OAuth2Settings();

        var error = await FailsAsync(request);

        Assert.Equal(RequestErrorKind.Auth, error.Kind);
        Assert.Contains("token URL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignsARequestWithOAuth1()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth?page=2"));
        request.Auth = new OAuth1Settings
        {
            ConsumerKey = "consumer",
            ConsumerSecret = "consumer-secret",
            Token = "user-token",
            TokenSecret = "user-secret",
            SignatureMethod = OAuth1Signature.HmacSha1,
            Realm = "things",
        };

        var header = Text(BodyJson((await SendAsync(request)).Body), "authorization")!;

        Assert.StartsWith("OAuth ", header, StringComparison.Ordinal);
        foreach (var expected in new[]
        {
            "realm=\"things\"",
            "oauth_consumer_key=\"consumer\"",
            "oauth_token=\"user-token\"",
            "oauth_signature_method=\"HMAC-SHA1\"",
            "oauth_version=\"1.0\"",
            "oauth_nonce=",
            "oauth_timestamp=",
            "oauth_signature=",
        })
        {
            Assert.Contains(expected, header, StringComparison.Ordinal);
        }

        // The signature is base64 and percent-encoded, so it never contains a raw
        // `+` or `=`; those would break the header the server parses.
        var parts = header.Split("oauth_signature=\"");
        var signature = parts.Length > 1 ? parts[1].Split('"')[0] : "";
        Assert.NotEmpty(signature);
        Assert.DoesNotContain('+', signature);
    }

    [Fact]
    public async Task OmitsTheTokenWhenThereIsNotOneYet()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Auth = new OAuth1Settings { ConsumerKey = "consumer", ConsumerSecret = "consumer-secret" };

        var header = Text(BodyJson((await SendAsync(request)).Body), "authorization")!;

        Assert.DoesNotContain("oauth_token=", header, StringComparison.Ordinal);
        Assert.DoesNotContain("realm=", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaintextSigningSendsTheKeyItself()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Auth = new OAuth1Settings
        {
            ConsumerKey = "consumer",
            ConsumerSecret = "consumer-secret",
            TokenSecret = "user-secret",
            SignatureMethod = OAuth1Signature.Plaintext,
        };

        var header = Text(BodyJson((await SendAsync(request)).Body), "authorization")!;

        // `consumer-secret&user-secret`, percent-encoded once for the header.
        Assert.Contains("oauth_signature=\"consumer-secret%26user-secret\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletesAnNtlmHandshake()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/ntlm"));
        request.Auth = new NtlmAuth
        {
            Username = "ada",
            Password = "lovelace",
            Domain = "LOVELACE-LTD",
            Workstation = "analytical-engine",
        };

        var response = await SendAsync(request);

        // The server recomputes the NTLMv2 proof, so 200 means the arithmetic was
        // right — and it only had a challenge to check against because all three
        // messages arrived on one connection.
        Assert.True(response.Status == 200, $"handshake failed: {Encoding.UTF8.GetString(response.Body)}");
        var body = BodyJson(response.Body);
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal("ada", Text(body, "user"));

        // Two requests on this connection: the negotiate and the authenticate. The
        // 401 that started it all came from the shared client on another one, which
        // is what it is for. Had these two been split across sockets, the server
        // would have had no challenge to check the second against.
        Assert.Equal(2, body.GetProperty("requestsOnThisConnection").GetInt32());
    }

    [Fact]
    public async Task AWrongNtlmPasswordIsRefused()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/ntlm"));
        request.Auth = new NtlmAuth { Username = "ada", Password = "not-the-password", Domain = "LOVELACE-LTD" };

        var response = await SendAsync(request);

        Assert.Equal(401, response.Status);
        Assert.Equal("bad proof", Text(BodyJson(response.Body), "error"));
    }

    [Fact]
    public async Task NtlmLeavesAServerThatWantsSomethingElseAlone()
    {
        await using var server = await LocalServer.StartAsync();
        // /digest challenges with Digest, not NTLM.
        var request = HttpRequest.Get(server.Url("/digest"));
        request.Auth = new NtlmAuth { Username = "ada", Password = "lovelace" };

        var response = await SendAsync(request);

        Assert.Equal(401, response.Status);
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task NtlmSendsNothingUntilItIsAsked()
    {
        await using var server = await LocalServer.StartAsync();
        var request = HttpRequest.Get(server.Url("/auth"));
        request.Auth = new NtlmAuth { Username = "ada", Password = "lovelace" };

        Assert.Equal("", Text(BodyJson((await SendAsync(request)).Body), "authorization"));
    }
}
