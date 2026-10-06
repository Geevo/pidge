using System.Text.Json;
using System.Text.Json.Nodes;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Session.Tests;

public class ExportTests
{
    private static SavedRequest SavedWith(AuthConfig auth, List<KeyValueEntry> headers)
    {
        var request = HttpRequest.Get("https://api.example.com/users");
        request.Auth = auth;
        request.Headers = headers;
        return new SavedRequest("Users", request);
    }

    [Fact]
    public void SecretsBecomeVariablesNamedForThem()
    {
        var saved = SavedWith(
            new OAuth2Settings { ClientId = "my-client", ClientSecret = "shh", RefreshToken = "rt-1" },
            [new KeyValueEntry("X-API-Key", "k-123"), new KeyValueEntry("Accept", "application/json")]);

        var json = Export.Write([saved], ExportFormat.Json, includeSecrets: false);
        foreach (var secret in new[] { "shh", "rt-1", "k-123" })
        {
            Assert.DoesNotContain(secret, json);
        }
        foreach (var kept in new[] { "{{clientSecret}}", "{{refreshToken}}", "{{xApiKey}}", "my-client", "application/json" })
        {
            Assert.Contains(kept, json);
        }

        // The saved request itself is untouched.
        Assert.Equal("shh", ((OAuth2Settings)saved.Request.Auth).ClientSecret);
    }

    [Fact]
    public void AnApiKeyIsNamedForWhatItIs()
    {
        var saved = SavedWith(new ApiKeyAuth { Key = "X-Key", Value = "k-1" }, []);
        Assert.Contains("{{apiKey}}", Export.Write([saved], ExportFormat.Json, includeSecrets: false));
    }

    [Fact]
    public void AskedToIncludeThemItDoes()
    {
        var saved = SavedWith(new BearerAuth { Token = "tok-1" }, []);
        Assert.Contains("Authorization: Bearer tok-1", Export.Write([saved], ExportFormat.Http, includeSecrets: true));
    }

    [Fact]
    public void AValueThatIsAlreadyAVariableIsLeftAlone()
    {
        var saved = SavedWith(
            new BearerAuth { Token = "{{myToken}}" },
            [new KeyValueEntry("Authorization", "Bearer {{other}}")]);
        var json = Export.Write([saved], ExportFormat.Json, includeSecrets: false);
        Assert.Contains("{{myToken}}", json);
        Assert.Contains("Bearer {{other}}", json);
    }

    [Fact]
    public void ASecretBesideAVariableIsStillASecret()
    {
        var saved = SavedWith(new NoAuth(), [new KeyValueEntry("Cookie", "session=abc; theme={{theme}}")]);
        var json = Export.Write([saved], ExportFormat.Json, includeSecrets: false);
        Assert.DoesNotContain("session=abc", json);
        Assert.Contains("{{cookie}}", json);
    }

    [Fact]
    public void TheJsonSaysWhatItIsAndRoundTrips()
    {
        var saved = SavedWith(new NoAuth(), []);
        var json = Export.Write([saved], ExportFormat.Json, includeSecrets: true);
        var value = JsonNode.Parse(json)!;

        Assert.Equal("pidge/saved-requests", (string?)value["kind"]);
        var back = JsonSerializer.Deserialize(value["savedRequests"]!.ToJsonString(), StorageJsonContext.Wire.ListSavedRequest)!;
        Assert.Single(back);
        Assert.True(PidgeJson.Same(saved, back[0], StorageJsonContext.Wire.SavedRequest));
    }

    [Fact]
    public void TheJsonIsLaidOutAsTheDesktopAppWritesIt()
    {
        var json = Export.Write([], ExportFormat.Json, includeSecrets: true);
        Assert.Equal("{\n  \"kind\": \"pidge/saved-requests\",\n  \"version\": 1,\n  \"savedRequests\": []\n}\n", json);
    }

    [Theory]
    [InlineData("X-API-Key", "xApiKey")]
    [InlineData("Authorization", "authorization")]
    [InlineData("x-amz-security-token", "xAmzSecurityToken")]
    [InlineData("--api__key--", "apiKey")]
    public void HeaderNamesBecomeVariableNames(string header, string expected) =>
        Assert.Equal(expected, Export.CamelCase(header));

    [Theory]
    [InlineData("{{token}}", true)]
    [InlineData("Bearer {{token}}", true)]
    [InlineData("  bot {{a}}{{b}} ", true)]
    [InlineData("Bearer tok", false)]
    [InlineData("{{token", false)]
    [InlineData("x{{token}}", false)]
    [InlineData("", false)]
    public void OnlyAVariableOrASchemeAndAVariableCountsAsAReference(string value, bool expected) =>
        Assert.Equal(expected, Export.OnlyReferences(value));

    [Fact]
    public void APlainSecretIsSpottedAndAReferenceIsNot()
    {
        Assert.True(Export.HoldsPlainSecret(SavedWith(new BasicAuth { Username = "ada", Password = "s3cret" }, []).Request));
        Assert.False(Export.HoldsPlainSecret(SavedWith(new BearerAuth { Token = "{{token}}" }, []).Request));
        Assert.True(Export.HoldsPlainSecret(SavedWith(new NoAuth(), [new KeyValueEntry("X-Api-Key", "k")]).Request));
        Assert.False(Export.HoldsPlainSecret(SavedWith(new NoAuth(), [new KeyValueEntry("Accept", "text/html")]).Request));
    }
}
