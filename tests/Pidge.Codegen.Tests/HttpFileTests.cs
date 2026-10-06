using Pidge.Core;

namespace Pidge.Codegen.Tests;

public class HttpFileTests
{
    private static string Write(params (string Name, HttpRequest Request)[] requests) => HttpFile.Write(requests);

    [Fact]
    public void WritesANamedBlockWithHeadersAndAJsonBody()
    {
        var request = HttpRequest.Get("https://api.example.com/users");
        request.Method = RequestMethod.Post;
        request.Headers.Add(KeyValueEntry.New("Accept", "application/json"));
        request.Headers.Add(KeyValueEntry.Disabled("X-Debug", "1"));
        request.Body = new JsonBody { Text = "{\"name\": \"Ada\"}" };

        Assert.Equal(
            "### Create user\n"
            + "POST https://api.example.com/users\n"
            + "Accept: application/json\n"
            + "Content-Type: application/json\n"
            + "\n"
            + "{\"name\": \"Ada\"}\n",
            Write(("Create user", request)));
    }

    [Fact]
    public void KeepsVariablesAsVariables()
    {
        var request = HttpRequest.Get("{{baseUrl}}/users");
        request.Auth = new BearerAuth { Token = "{{token}}" };
        request.QueryParams.Add(KeyValueEntry.New("q", "{{name}} smith"));

        var file = Write(("Search", request));
        Assert.Contains("GET {{baseUrl}}/users?q={{name}}%20smith\n", file, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer {{token}}\n", file, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotRepeatAParameterTheUrlAlreadyCarries()
    {
        var request = HttpRequest.Get("https://x.test/a?postcode=SW1A%201AA");
        request.QueryParams.Add(KeyValueEntry.New("postcode", "SW1A 1AA"));
        Assert.Contains("GET https://x.test/a?postcode=SW1A%201AA\n", Write(("A", request)), StringComparison.Ordinal);
    }

    [Fact]
    public void BasicAuthIsFinishedUnlessItHoldsVariables()
    {
        var request = HttpRequest.Get("https://x.test");
        request.Auth = new BasicAuth { Username = "ada", Password = "s3cret" };
        Assert.Contains("Authorization: Basic YWRhOnMzY3JldA==\n", Write(("A", request)), StringComparison.Ordinal);

        request.Auth = new BasicAuth { Username = "ada", Password = "{{password}}" };
        Assert.Contains("Authorization: Basic ada {{password}}\n", Write(("A", request)), StringComparison.Ordinal);
    }

    [Fact]
    public void ATypedAuthorizationHeaderWinsOverTheAuthTab()
    {
        var request = HttpRequest.Get("https://x.test");
        request.Headers.Add(KeyValueEntry.New("Authorization", "Token abc"));
        request.Auth = new BearerAuth { Token = "xyz" };

        var file = Write(("A", request));
        Assert.Contains("Authorization: Token abc\n", file, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", file, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhatItCannotWrite()
    {
        var request = HttpRequest.Get("https://x.test");
        request.Auth = new OAuth2Settings();
        Assert.Contains("# OAuth 2 is left out", Write(("A", request)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnApiKeyGoesWhereItWasPlaced()
    {
        var request = HttpRequest.Get("https://x.test/a");
        request.Auth = new ApiKeyAuth { Key = "api_key", Value = "k 1", Placement = ApiKeyPlacement.Query };
        Assert.Contains("GET https://x.test/a?api_key=k%201\n", Write(("A", request)), StringComparison.Ordinal);
    }

    [Fact]
    public void MultipartReadsFilesIn()
    {
        var request = HttpRequest.Get("https://x.test/upload");
        request.Method = RequestMethod.Post;
        request.Body = new MultipartBody
        {
            Entries =
            [
                MultipartEntry.Text("title", "Holiday"),
                MultipartEntry.File("photo", "/home/ada/beach.jpg"),
            ],
        };

        var file = Write(("Upload", request));
        Assert.Contains("Content-Type: multipart/form-data; boundary=pidge-boundary\n", file, StringComparison.Ordinal);
        Assert.Contains(
            "name=\"photo\"; filename=\"beach.jpg\"\n\n< /home/ada/beach.jpg\n",
            file,
            StringComparison.Ordinal);
        Assert.EndsWith("--pidge-boundary--\n", file, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparatesRequestsWithABlankLine()
    {
        var one = HttpRequest.Get("https://x.test/1");
        var two = HttpRequest.Get("https://x.test/2");
        Assert.Equal(
            "### One\nGET https://x.test/1\n\n### Two\nGET https://x.test/2\n",
            Write(("One", one), ("Two", two)));
    }
}
