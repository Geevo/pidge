namespace Pidge.Codegen.Tests;

public class CurlTests
{
    [Fact]
    public void AQuoteInsideAValueSurvives() => Assert.Equal("'it'\\''s'", Curl.Quote("it's"));
}
