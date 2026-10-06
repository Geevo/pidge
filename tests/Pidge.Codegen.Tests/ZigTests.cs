namespace Pidge.Codegen.Tests;

public class ZigTests
{
    [Fact]
    public void APlainValueIsAQuotedString() =>
        Assert.Equal("\"application/json\"", Zig.Literal("application/json"));

    /// <summary>
    /// Every line of a multiline string carries its own <c>\\</c>, and nothing
    /// inside one is an escape.
    /// </summary>
    [Fact]
    public void ABodyOverSeveralLinesIsAMultilineString() =>
        Assert.Equal("\\\\{\n\\\\  \"a\": 1\n\\\\}", Zig.Literal("{\n  \"a\": 1\n}"));
}
