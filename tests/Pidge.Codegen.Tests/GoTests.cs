namespace Pidge.Codegen.Tests;

public class GoTests
{
    [Fact]
    public void ABodyOverSeveralLinesIsARawString() =>
        Assert.Equal("`{\n  \"a\": 1\n}`", Text.Settle(Go.Literal("{\n  \"a\": 1\n}")));

    /// <summary>A raw string cannot carry a backtick, so that one is escaped instead.</summary>
    [Fact]
    public void ABacktickForcesTheQuotedForm() => Assert.Equal("\"a\\n`b\"", Go.Literal("a\n`b"));
}
