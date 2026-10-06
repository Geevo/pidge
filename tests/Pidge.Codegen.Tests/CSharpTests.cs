namespace Pidge.Codegen.Tests;

public class CSharpTests
{
    [Fact]
    public void AQuoteIsDoubled() => Assert.Equal("@\"{\"\"a\"\": 1}\"", CSharp.Literal("{\"a\": 1}"));

    /// <summary>Nothing to escape, nothing to explain.</summary>
    [Fact]
    public void APlainValueIsAPlainLiteral() =>
        Assert.Equal("\"application/json\"", CSharp.Literal("application/json"));

    /// <summary>The reason for verbatim strings: this is a path, not four escapes.</summary>
    [Fact]
    public void AWindowsPathSurvives() => Assert.Equal("@\"C:\\temp\\a.png\"", CSharp.Literal("C:\\temp\\a.png"));
}
