namespace NovaTerminal.VT.Tests;

// Regression tests for #150: regex patterns that produce zero-length matches (e.g. "a*",
// "()", "\b") crashed FindMatches with ArgumentOutOfRangeException — thrown while holding
// the buffer read lock — because colMapping was indexed at m.Index + m.Length - 1 == -1.
public class FindMatchesTests
{
    private static (TerminalBuffer buffer, AnsiParser parser) CreateTerminal(string content = "foo bar foo")
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 5);
        var parser = new AnsiParser(buffer);
        parser.Process(content);
        return (buffer, parser);
    }

    [Theory]
    [InlineData("x*")]      // zero-length match at every position
    [InlineData("()")]      // empty group, zero-length everywhere
    [InlineData("\\b")]     // word boundary, zero-length
    [InlineData("(?=foo)")] // lookahead, zero-length
    [InlineData("$")]       // end anchor, zero-length at end-of-line
    public void ZeroLengthRegexMatches_DoNotThrow(string pattern)
    {
        var (buffer, _) = CreateTerminal();

        var ex = Record.Exception(() => buffer.FindMatches(pattern, useRegex: true, caseSensitive: false));

        Assert.Null(ex);
    }

    [Fact]
    public void ZeroLengthMatches_AreSkipped_ButRealMatchesStillFound()
    {
        var (buffer, _) = CreateTerminal("foo bar foo");

        // "o*" matches zero-length everywhere but also "oo" twice.
        var matches = buffer.FindMatches("o*", useRegex: true, caseSensitive: false);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.True(m.EndCol >= m.StartCol));
    }

    [Fact]
    public void PlainRegexSearch_StillWorks()
    {
        var (buffer, _) = CreateTerminal("foo bar foo");

        var matches = buffer.FindMatches("foo", useRegex: true, caseSensitive: true);

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].StartCol);
        Assert.Equal(2, matches[0].EndCol);
        Assert.Equal(8, matches[1].StartCol);
        Assert.Equal(10, matches[1].EndCol);
    }
}
