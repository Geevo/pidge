namespace Pidge.Codegen.Tests;

public class NodeTests
{
    [Fact]
    public void ABodyOverSeveralLinesKeepsThem() =>
        Assert.Equal("`{\n  \"a\": 1\n}`", Node.Literal("{\n  \"a\": 1\n}"));

    [Fact]
    public void ATemplateLiteralEscapesWhatWouldEndIt() =>
        Assert.Equal("`a\n\\`\\${x}`", Node.Literal("a\n`${x}"));

    [Fact]
    public void AHeaderNameWithADashIsQuoted()
    {
        Assert.Equal("Accept", Node.Key("Accept"));
        Assert.Equal("\"X-Trace\"", Node.Key("X-Trace"));
    }
}
