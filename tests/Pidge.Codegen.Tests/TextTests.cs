using Pidge.Core;

namespace Pidge.Codegen.Tests;

public class TextTests
{
    [Fact]
    public void SecondsDropAWholeNumberOfZeros()
    {
        Assert.Equal("30", Text.Seconds(30_000));
        Assert.Equal("1.5", Text.Seconds(1_500));
        Assert.Equal("0", Text.Seconds(0));
    }

    /// <summary>The one place <c>+</c> means space. The query string is not this.</summary>
    [Fact]
    public void AFormBodyWritesASpaceAsAPlus() =>
        Assert.Equal("full+name=Ada+Lovelace", Text.FormEncoded([KeyValueEntry.New("full name", "Ada Lovelace")]));

    [Fact]
    public void APlusInAFormValueIsEscaped() =>
        Assert.Equal("phone=%2B44+7700+900000", Text.FormEncoded([KeyValueEntry.New("phone", "+44 7700 900000")]));

    /// <summary>A body's own indentation is data, and survives the code around it being laid out.</summary>
    [Fact]
    public void TheInsideOfALiteralIsNotIndented()
    {
        var literal = Text.Keep("r#\"{\n  \"a\": 1\n}\"#");
        var block = Text.Indent($"let payload = {literal};", 4);

        Assert.Equal("    let payload = r#\"{\n  \"a\": 1\n}\"#;", Text.Settle(block));
    }

    [Fact]
    public void ANoteIsWrappedRatherThanLeftToRunOff()
    {
        var note = Text.Comment("# ", string.Concat(Enumerable.Repeat("word ", 30)));
        var lines = Text.Lines(note);
        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.True(Text.Utf8Length(line) <= 80, line));
        Assert.All(lines, line => Assert.StartsWith("# ", line, StringComparison.Ordinal));
    }

    /// <summary>A command is there to be copied, and three lines of one cannot be.</summary>
    [Fact]
    public void AnIndentedLineIsLeftExactlyAsItIs()
    {
        const string command = "openssl pkcs12 -in /a/very/long/path/to/a/client/bundle.p12 "
            + "-out /a/very/long/path/to/a/client/bundle.pem -nodes";
        var note = Text.Comment("# ", $"Convert it first:\n  {command}");

        var lines = Text.Lines(note);
        Assert.Equal("# Convert it first:", lines[0]);
        Assert.Equal($"#   {command}", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void RepeatedHeaderNamesAreFoldedIntoOne()
    {
        List<(string Name, string Value)> headers =
        [
            ("Accept", "text/html"),
            ("accept", "application/json"),
        ];
        var (folded, collided) = Text.Fold(headers);
        Assert.True(collided);
        Assert.Equal([("Accept", "text/html, application/json")], folded);
    }
}
