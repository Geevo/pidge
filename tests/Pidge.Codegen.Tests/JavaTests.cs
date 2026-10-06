namespace Pidge.Codegen.Tests;

public class JavaTests
{
    [Fact]
    public void APlainValueIsAQuotedString() =>
        Assert.Equal("\"application/json\"", Java.Literal("application/json"));

    /// <summary>The content sits at the margin so that nothing is stripped from it.</summary>
    [Fact]
    public void ABodyOverSeveralLinesIsATextBlock() =>
        Assert.Equal("\"\"\"\n{\n  \"a\": 1\n}\"\"\"", Text.Settle(Java.Literal("{\n  \"a\": 1\n}")));
}
