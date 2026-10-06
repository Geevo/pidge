using Pidge.Core;
using Pidge.Variables;

namespace Pidge.Variables.Tests;

public class SubstitutionTests
{
    private static VariableSet Vars() =>
        new([("baseUrl", "https://api.example.com"), ("token", "abc123")]);

    private static string Ok(string input) =>
        Substitution.Substitute(input, Vars(), out _) ?? throw new Xunit.Sdk.XunitException("missing variables");

    [Fact]
    public void ReplacesKnownVariables() =>
        Assert.Equal("https://api.example.com/users", Ok("{{baseUrl}}/users"));

    [Fact]
    public void ReplacesSeveralOccurrences() => Assert.Equal("abc123-abc123", Ok("{{token}}-{{token}}"));

    [Fact]
    public void ToleratesWhitespaceInsideTheBraces() => Assert.Equal("abc123", Ok("{{  token  }}"));

    [Fact]
    public void TextWithoutVariablesIsUntouched() =>
        Assert.Equal("https://example.com/x", Ok("https://example.com/x"));

    [Fact]
    public void AnUnclosedPlaceholderStaysLiteral() => Assert.Equal("a {{token b", Ok("a {{token b"));

    [Fact]
    public void ReportsEveryMissingNameAtOnce()
    {
        Assert.Null(Substitution.Substitute("{{a}}/{{b}}/{{a}}", Vars(), out var missing));
        Assert.Equal(["a", "b"], missing);
    }

    [Fact]
    public void ResolvesAcrossTheWholeRequest()
    {
        var request = HttpRequest.Get("{{baseUrl}}/users");
        request.QueryParams = [KeyValueEntry.New("key", "{{token}}")];
        request.Headers = [KeyValueEntry.New("X-Token", "{{token}}")];
        request.Auth = new BearerAuth { Token = "{{token}}" };
        request.Body = new JsonBody { Text = """{"url":"{{baseUrl}}"}""" };

        var resolved = Substitution.ResolveRequest(request, Vars());

        Assert.Equal("https://api.example.com/users", resolved.Url);
        Assert.Equal("abc123", resolved.QueryParams[0].Value);
        Assert.Equal("abc123", resolved.Headers[0].Value);
        Assert.Equal("abc123", Assert.IsType<BearerAuth>(resolved.Auth).Token);
        Assert.Equal("""{"url":"https://api.example.com"}""", Assert.IsType<JsonBody>(resolved.Body).Text);
    }

    [Fact]
    public void AnUnresolvedVariableIsANormalizedError()
    {
        var request = HttpRequest.Get("{{nope}}/users");
        var error = Assert.Throws<RequestErrorException>(() => Substitution.ResolveRequest(request, Vars())).Error;

        Assert.Equal(RequestErrorKind.UnresolvedVariable, error.Kind);
        Assert.Contains("{{nope}}", error.Message);
        Assert.Contains("baseUrl", error.Detail);
    }

    [Fact]
    public void DisabledRowsNeverFailOnTheirVariables()
    {
        var request = HttpRequest.Get("{{baseUrl}}");
        request.Headers = [KeyValueEntry.Disabled("X-Old", "{{retired}}")];

        var resolved = Substitution.ResolveRequest(request, Vars());
        Assert.Equal("{{retired}}", resolved.Headers[0].Value);
    }

    [Fact]
    public void AnEnvironmentContributesOnlyItsEnabledRows()
    {
        var environment = new VariableEnvironment
        {
            Id = "env",
            Name = "Local",
            Variables =
            [
                KeyValueEntry.New("host", "localhost"),
                KeyValueEntry.Disabled("host2", "elsewhere"),
                KeyValueEntry.New("", "nameless"),
            ],
        };

        var set = VariableSet.From(environment);
        Assert.Equal("localhost", set.Get("host"));
        Assert.Null(set.Get("host2"));
        Assert.Equal(["host"], set.Names);
    }

    [Fact]
    public void MissingVariablesKeepsFirstAppearanceOrder()
    {
        var request = HttpRequest.Get("{{z}}/{{a}}");
        request.Headers = [KeyValueEntry.New("X", "{{z}}{{m}}")];
        Assert.Equal(["z", "a", "m"], Substitution.MissingVariables(request, Vars()));
    }
}
