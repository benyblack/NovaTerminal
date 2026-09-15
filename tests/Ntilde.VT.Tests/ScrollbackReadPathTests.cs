using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// #95 gap 1: the scrollback *storage* already carried row side tables (see
// ScrollbackSideTableTests), but the read accessors did not consult them -
// GetGraphemeAbsolute returned cell.Character.ToString() and GetHyperlinkAbsolute
// returned null unconditionally for any row in scrollback. So a multi-codepoint
// grapheme silently degraded to its first UTF-16 char, and an OSC 8 link stopped being a
// link, the moment the line scrolled off screen. Both accessors feed selection/copy and
// link detection, so the loss was user-visible in both.
public class ScrollbackReadPathTests
{
    /// Writes enough lines to push the first one into scrollback, and returns the buffer.
    private static TerminalBuffer BufferWithFirstRowScrolledOff(string firstLine, int rows = 3)
    {
        var buffer = new TerminalBuffer(20, rows);
        var parser = new AnsiParser(buffer);
        parser.Process(firstLine + "\r\n");
        for (int i = 0; i < rows; i++)
        {
            parser.Process($"filler {i}\r\n");
        }
        return buffer;
    }

    [Fact]
    public void GetGraphemeAbsolute_ReturnsTheFullGraphemeFromScrollback()
    {
        // Astral-plane emoji: two UTF-16 code units, so the old path returned a lone
        // unpaired surrogate rather than the character.
        var buffer = BufferWithFirstRowScrolledOff("ok \U0001F44D done");

        Assert.True(buffer.Scrollback.Count > 0, "first row should have scrolled off");

        buffer.Lock.EnterReadLock();
        try
        {
            string grapheme = buffer.GetGraphemeAbsolute(3, 0);
            Assert.Equal("\U0001F44D", grapheme);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void GetGraphemeAbsolute_StillReturnsPlainCharactersFromScrollback()
    {
        // The fallback must survive: rows with no side table are the common case.
        var buffer = BufferWithFirstRowScrolledOff("plain text");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("p", buffer.GetGraphemeAbsolute(0, 0));
            Assert.Equal("l", buffer.GetGraphemeAbsolute(1, 0));
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void GetHyperlinkAbsolute_ReturnsTheUriFromScrollback()
    {
        const string uri = "https://example.com/scrolled-off";
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        // OSC 8 open, text, OSC 8 close.
        parser.Process($"\x1b]8;;{uri}\x1b\\linked\x1b]8;;\x1b\\\r\n");
        for (int i = 0; i < 3; i++)
        {
            parser.Process($"filler {i}\r\n");
        }

        Assert.True(buffer.Scrollback.Count > 0, "linked row should have scrolled off");
        Assert.Equal(uri, buffer.GetHyperlinkAbsolute(0, 0));
    }

    [Fact]
    public void GetHyperlinkAbsolute_ReturnsNullForUnlinkedScrollbackCells()
    {
        var buffer = BufferWithFirstRowScrolledOff("no links here");

        Assert.Null(buffer.GetHyperlinkAbsolute(0, 0));
    }
}
