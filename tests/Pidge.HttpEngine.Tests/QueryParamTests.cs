using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.HttpEngine.Tests;

/// <summary>How query parameters are merged into the URL.</summary>
public class QueryParamTests
{
    private static string QueryFor(string url, List<KeyValueEntry> parameters, bool encodeQuery = true)
    {
        var request = new HttpRequest { Url = url, QueryParams = parameters, EncodeQuery = encodeQuery };
        var normalized = UrlInput.NormalizeUrl(request.Url);
        RequestPlanning.AppendQueryParams(normalized, request);
        return normalized.Query ?? "";
    }

    /*
     * A space is `%20` in a URL. `+` means space only in a form body, and a
     * server that takes the query literally answers 400 to the plus.
     */
    [Fact]
    public void ASpaceInAValueIsPercentEncoded() =>
        Assert.Equal(
            "postcode=SW1A%201AA",
            QueryFor("https://example.com/lookup", [KeyValueEntry.New("postcode", "SW1A 1AA")]));

    /// <summary>A plus the user typed is a plus, not a space.</summary>
    [Fact]
    public void ALiteralPlusSurvives() =>
        Assert.Equal(
            "phone=%2B44%207700%20900000",
            QueryFor("https://example.com/c", [KeyValueEntry.New("phone", "+44 7700 900000")]));

    /// <summary>The query already in the URL is the user's own text and is not rewritten.</summary>
    [Fact]
    public void TheTypedQueryIsLeftExactlyAsTyped() =>
        Assert.Equal(
            "filter=a%20b&raw=x+y&path=/v1/items&page=2",
            QueryFor("https://example.com/s?filter=a%20b&raw=x+y&path=/v1/items", [KeyValueEntry.New("page", "2")]));

    /// <summary>The table and the URL hold the same pair; it goes out once.</summary>
    [Fact]
    public void ARowTheUrlAlreadyCarriesIsNotSentTwice() =>
        Assert.Equal(
            "postcode=SW1A%201AA",
            QueryFor("https://example.com/lookup?postcode=SW1A%201AA", [KeyValueEntry.New("postcode", "SW1A 1AA")]));

    /// <summary>Matching is on the decoded pair, so the spelling of the encoding is moot.</summary>
    [Fact]
    public void TheMatchIgnoresHowEachSideEncodedIt() =>
        Assert.Equal("q=a+b", QueryFor("https://example.com/lookup?q=a+b", [KeyValueEntry.New("q", "a b")]));

    /// <summary>A row that differs from the URL's is a second parameter, not a duplicate.</summary>
    [Fact]
    public void ARowWithAnotherValueIsStillAppended() =>
        Assert.Equal("tag=red&tag=blue", QueryFor("https://example.com/s?tag=red", [KeyValueEntry.New("tag", "blue")]));

    /// <summary>Two identical rows are two copies on purpose; only the URL is deduped against.</summary>
    [Fact]
    public void TheTableMayRepeatItself() =>
        Assert.Equal(
            "id=7&id=7",
            QueryFor("https://example.com/s", [KeyValueEntry.New("id", "7"), KeyValueEntry.New("id", "7")]));

    /// <summary>The <c>/</c> and <c>:</c> survive, and the already-encoded value is not encoded twice.</summary>
    [Fact]
    public void EncodingCanBeTurnedOffForARequest() =>
        Assert.Equal(
            "path=/v1/a:b&pre=%2F",
            QueryFor(
                "https://example.com/s",
                [KeyValueEntry.New("path", "/v1/a:b"), KeyValueEntry.New("pre", "%2F")],
                encodeQuery: false));

    [Fact]
    public void EncodingOnEscapesTheSameValues() =>
        Assert.Equal(
            "path=%2Fv1%2Fa%3Ab&pre=%252F",
            QueryFor("https://example.com/s", [KeyValueEntry.New("path", "/v1/a:b"), KeyValueEntry.New("pre", "%2F")]));

    /// <summary>A space cannot appear in a request line at all, so it is escaped even with encoding off.</summary>
    [Fact]
    public void ASpaceIsEscapedEvenWithEncodingOff() =>
        Assert.Equal(
            "postcode=SW1A%201AA",
            QueryFor("https://example.com/lookup", [KeyValueEntry.New("postcode", "SW1A 1AA")], encodeQuery: false));

    [Fact]
    public void SeparatorsInsideAValueCannotSplitIt() =>
        Assert.Equal("q=a%26b%3Dc%23d", QueryFor("https://example.com/s", [KeyValueEntry.New("q", "a&b=c#d")]));

    [Theory]
    [InlineData("localhost:3000/test", "http://localhost:3000/test")]
    [InlineData("  example.com  ", "http://example.com/")]
    [InlineData("HTTPS://Example.com/x", "https://example.com/x")]
    public void BareInputsGetHttp(string input, string expected) =>
        Assert.Equal(expected, UrlInput.NormalizeUrl(input).ToString());

    [Theory]
    [InlineData("", RequestErrorKind.InvalidUrl, "Enter a URL.")]
    [InlineData("ftp://example.com", RequestErrorKind.UnsupportedScheme, "ftp:// is not supported. Use http:// or https://.")]
    [InlineData("http://exa mple.com", RequestErrorKind.InvalidUrl, "`http://exa mple.com` is not a valid URL.")]
    public void BadInputIsANormalizedError(string input, RequestErrorKind kind, string message)
    {
        var error = Assert.Throws<RequestErrorException>(() => UrlInput.NormalizeUrl(input)).Error;
        Assert.Equal(kind, error.Kind);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void AnExplicitAuthorizationHeaderWins()
    {
        var request = HttpRequest.Get("https://example.com");
        request.Headers = [KeyValueEntry.New("authorization", "Token x")];
        request.Auth = new BearerAuth { Token = "t" };

        var effective = RequestPlanning.Effective(request);

        Assert.IsType<AuthPlan.Nothing>(effective.Auth);
        Assert.Equal([("authorization", "Token x")], effective.Headers);
        Assert.Equal(["An explicit Authorization header is set, so the Auth tab was ignored."], effective.Warnings);
    }

    [Fact]
    public void HeadersComeInTheirOrder()
    {
        var request = HttpRequest.Get("https://example.com");
        request.Method = RequestMethod.Post;
        request.Headers = [KeyValueEntry.New("X-A", "1")];
        request.Auth = new BasicAuth { Username = "u", Password = "p" };
        request.Body = new JsonBody { Text = "{}" };

        var effective = RequestPlanning.Effective(request);

        Assert.Equal(
            [("X-A", "1"), ("Authorization", "Basic dTpw"), ("Content-Type", "application/json")],
            effective.Headers);
    }

    [Fact]
    public void AnApiKeyInTheQueryIsFoldedIntoTheUrl()
    {
        var request = HttpRequest.Get("https://example.com/x?a=1");
        request.Auth = new ApiKeyAuth { Key = " key ", Value = "v w", Placement = ApiKeyPlacement.Query };
        var effective = RequestPlanning.Effective(request);
        Assert.Equal("https://example.com/x?a=1&key=v%20w", effective.Url);
        Assert.IsType<AuthPlan.QueryParam>(effective.Auth);
    }

    [Fact]
    public void InvalidJsonIsCaughtBeforeSending()
    {
        var error = Assert.Throws<RequestErrorException>(() => RequestPlanning.CheckJson("{\n  \"a\": }")).Error;
        Assert.Equal(RequestErrorKind.BodySerialization, error.Kind);
        Assert.StartsWith("The JSON body is not valid JSON (line 2, column", error.Message);
    }
}
