namespace Pidge.Codegen.Tests;

public class PythonTests
{
    [Fact]
    public void ABackslashAndAQuoteAreEscaped() => Assert.Equal("\"a\\\\b\\\"c\"", Python.Literal("a\\b\"c"));

    [Fact]
    public void ABodyOverSeveralLinesIsWrittenAsItReads() =>
        Assert.Equal("\"\"\"{\n  \"a\": 1\n}\"\"\"", Python.Literal("{\n  \"a\": 1\n}"));

    /// <summary>
    /// A carriage return inside triple quotes is the source file's line ending,
    /// not data, so that text is escaped onto one line instead.
    /// </summary>
    [Fact]
    public void ACarriageReturnIsEscapedRatherThanWritten() =>
        Assert.Equal("\"a\\r\\nb\"", Python.Literal("a\r\nb"));
}
