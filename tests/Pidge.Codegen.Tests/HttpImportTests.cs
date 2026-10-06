using Pidge.Core;

namespace Pidge.Codegen.Tests;

public class HttpImportTests
{
    private static HttpRequest One(string text)
    {
        var parsed = HttpFile.Parse(text);
        Assert.Empty(parsed.Skipped);
        return Assert.Single(parsed.Requests).Request;
    }

    private static List<(string, string)> Headers(HttpRequest request) =>
        request.Headers.Select(header => (header.Name, header.Value)).ToList();

    private static void AssertBasic(AuthConfig auth, string username, string password)
    {
        var basic = Assert.IsType<BasicAuth>(auth);
        Assert.Equal(username, basic.Username);
        Assert.Equal(password, basic.Password);
    }

    [Fact]
    public void ReadsNamedRequestsWithHeadersAndAJsonBody()
    {
        var parsed = HttpFile.Parse(
            "### Create user\n"
            + "POST https://api.example.com/users HTTP/1.1\n"
            + "Accept: application/json\n"
            + "Content-Type: application/json\n"
            + "\n"
            + "{\"name\": \"Ada\"}\n"
            + "\n"
            + "### List users\n"
            + "GET https://api.example.com/users\n");

        Assert.Empty(parsed.Skipped);
        Assert.Equal(["Create user", "List users"], parsed.Requests.Select(r => r.Name));

        var create = parsed.Requests[0].Request;
        Assert.Equal(RequestMethod.Post, create.Method);
        Assert.Equal("https://api.example.com/users", create.Url);
        Assert.Equal([("Accept", "application/json")], Headers(create));
        Assert.Equal("{\"name\": \"Ada\"}", Assert.IsType<JsonBody>(create.Body).Text);
    }

    [Fact]
    public void ABareUrlIsAGetAndCommentsAreIgnored()
    {
        var request = One("# the health check\n// and another comment\nhttps://x.test/health\n");
        Assert.Equal(RequestMethod.Get, request.Method);
        Assert.Equal("https://x.test/health", request.Url);
    }

    [Fact]
    public void SubstitutesFileVariablesAndKeepsTheRest()
    {
        var request = One(
            "@host = https://api.example.com\n"
            + "@base = {{host}}/v2\n"
            + "\n"
            + "###\n"
            + "GET {{base}}/users?id={{$guid}}\n"
            + "Authorization: Bearer {{token}}\n");
        Assert.Equal("https://api.example.com/v2/users?id={{$guid}}", request.Url);
        Assert.Equal("{{token}}", Assert.IsType<BearerAuth>(request.Auth).Token);
        Assert.Empty(request.Headers);
    }

    [Fact]
    public void JoinsAQuerySpreadOverLines()
    {
        var request = One("GET https://x.test/search\n    ?q=rust\n    &page=2\n");
        Assert.Equal("https://x.test/search?q=rust&page=2", request.Url);
        Assert.Equal(
            [("q", "rust"), ("page", "2")],
            request.QueryParams.Select(p => (p.Name, p.Value)));
    }

    [Fact]
    public void ReadsBasicAuthInBothSpellings()
    {
        var spaced = One("GET https://x.test\nAuthorization: Basic ada s3cret\n");
        var encoded = One("GET https://x.test\nAuthorization: Basic YWRhOnMzY3JldA==\n");
        AssertBasic(spaced.Auth, "ada", "s3cret");
        AssertBasic(encoded.Auth, "ada", "s3cret");
    }

    [Fact]
    public void KeepsAnAuthorizationHeaderItHasNoTabFor()
    {
        var request = One("GET https://x.test\nAuthorization: Token abc\n");
        Assert.IsType<NoAuth>(request.Auth);
        Assert.Equal([("Authorization", "Token abc")], Headers(request));
    }

    [Fact]
    public void SkipsWhatItCannotSendAndSaysSo()
    {
        var parsed = HttpFile.Parse("### Trace it\nTRACE https://x.test\n\n### Fine\nGET https://x.test\n");
        Assert.Single(parsed.Requests);
        Assert.Equal(["“Trace it” was skipped: TRACE is not a method this app sends."], parsed.Skipped);
    }

    [Fact]
    public void LeavesScriptsAndResponseHandlersBehind()
    {
        var request = One(
            "< {%\n  request.variables.set(\"x\", 1)\n%}\n"
            + "POST https://x.test\n"
            + "Content-Type: text/plain\n"
            + "\n"
            + "hello\n"
            + "\n"
            + "> {%\n  client.test(\"ok\", () => {})\n%}\n");
        var body = Assert.IsType<TextBody>(request.Body);
        Assert.Equal("hello", body.Text);
        Assert.Equal("text/plain", body.ContentType);
        Assert.Empty(request.Headers);
    }

    [Fact]
    public void ReadsAFormBody()
    {
        var request = One(
            "POST https://x.test\nContent-Type: application/x-www-form-urlencoded\n\nname=Ada+Lovelace&city=London\n");
        var form = Assert.IsType<UrlEncodedBody>(request.Body);
        Assert.Equal(
            [("name", "Ada Lovelace"), ("city", "London")],
            form.Entries.Select(e => (e.Name, e.Value)));
    }

    [Fact]
    public void RoundTripsWhatTheWriterWrites()
    {
        var upload = HttpRequest.Get("https://x.test/upload");
        upload.Method = RequestMethod.Post;
        upload.Headers.Add(KeyValueEntry.New("X-Trace", "1"));
        upload.Body = new MultipartBody
        {
            Entries =
            [
                MultipartEntry.Text("title", "Holiday"),
                MultipartEntry.File("photo", "/home/ada/beach.jpg"),
            ],
        };

        var search = HttpRequest.Get("{{baseUrl}}/search?q={{name}}");
        search.QueryParams.Add(KeyValueEntry.New("q", "{{name}}"));
        search.Auth = new BasicAuth { Username = "ada", Password = "{{password}}" };

        var keyed = HttpRequest.Get("https://x.test/a");
        keyed.Auth = new ApiKeyAuth { Key = "X-API-Key", Value = "k", Placement = ApiKeyPlacement.Header };

        var written = HttpFile.Write([("Upload", upload), ("Search", search), ("Keyed", keyed)]);
        var parsed = HttpFile.Parse(written);
        Assert.Empty(parsed.Skipped);
        Assert.True(parsed.Requests.Count == 3, $"expected three requests: {written}");
        var (uploadBack, searchBack, keyedBack) = (parsed.Requests[0], parsed.Requests[1], parsed.Requests[2]);

        Assert.Equal("Upload", uploadBack.Name);
        Assert.Equal([("X-Trace", "1")], Headers(uploadBack.Request));
        var entries = Assert.IsType<MultipartBody>(uploadBack.Request.Body).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("Holiday", Assert.IsType<MultipartText>(entries[0].Value).Value);
        var file = Assert.IsType<MultipartFile>(entries[1].Value);
        Assert.Equal("/home/ada/beach.jpg", file.Path);
        Assert.Null(file.FileName);
        Assert.Null(file.ContentType);

        Assert.Equal(search.Url, searchBack.Request.Url);
        AssertBasic(searchBack.Request.Auth, "ada", "{{password}}");

        // An API key is a header like any other once it is in a file.
        Assert.Equal([("X-API-Key", "k")], Headers(keyedBack.Request));
    }

    [Fact]
    public void AuthTheWriterLeftOutDoesNotComeBackAsSomethingElse()
    {
        var request = HttpRequest.Get("https://x.test");
        request.Auth = new OAuth2Settings();
        var parsed = HttpFile.Parse(HttpFile.Write([("A", request)]));
        Assert.IsType<NoAuth>(parsed.Requests[0].Request.Auth);
    }
}
