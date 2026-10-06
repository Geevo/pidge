namespace Pidge.Codegen.Tests;

public class RustTests
{
    [Fact]
    public void APlainValueIsAPlainLiteral() =>
        Assert.Equal("\"application/json\"", Rust.Literal("application/json"));

    [Fact]
    public void AnythingWithAQuoteOrABackslashIsRaw()
    {
        Assert.Equal("r#\"{\"a\": 1}\"#", Rust.Literal("{\"a\": 1}"));
        Assert.Equal("r#\"C:\\temp\"#", Rust.Literal("C:\\temp"));
    }

    /// <summary>
    /// A body over several lines carries the mark that keeps its own
    /// indentation out of the indenter's hands.
    /// </summary>
    [Fact]
    public void ABodyOverSeveralLinesIsLeftWhereItIs() =>
        Assert.Equal("r#\"{\n  \"a\": 1\n}\"#", Text.Settle(Rust.Literal("{\n  \"a\": 1\n}")));

    /// <summary>A body carrying the closing fence needs a longer one.</summary>
    [Fact]
    public void TheFenceGrowsPastWhatTheBodyContains() =>
        Assert.Equal("r##\"say \"#\"##", Rust.Literal("say \"#"));
}
