namespace Pidge.Codegen.Tests;

public class PowerShellTests
{
    [Fact]
    public void AQuoteIsDoubled() => Assert.Equal("'it''s'", PowerShell.Quote("it's"));

    /// <summary>
    /// A dollar sign is a variable in a double-quoted string and plain text in
    /// a single-quoted one, which is why every string here is single-quoted.
    /// </summary>
    [Fact]
    public void ADollarSignIsLeftAlone() => Assert.Equal("'$body'", PowerShell.Quote("$body"));

    [Fact]
    public void MethodsAreWrittenAsTheDocumentationWritesThem() =>
        Assert.Equal("Delete", PowerShell.TitleCase("DELETE"));
}
