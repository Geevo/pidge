namespace Pidge.Codegen.Tests;

public class PhpTests
{
    /// <summary>
    /// A dollar sign is a variable in a double-quoted string and text in a
    /// single-quoted one, which is why every string here is single-quoted.
    /// </summary>
    [Fact]
    public void ADollarSignIsLeftAlone() => Assert.Equal("'$payload'", Php.Literal("$payload"));

    [Fact]
    public void AQuoteAndABackslashAreEscaped() => Assert.Equal("'it\\'s C:\\\\a'", Php.Literal("it's C:\\a"));
}
