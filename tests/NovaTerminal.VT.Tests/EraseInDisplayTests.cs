using System.Text;

namespace NovaTerminal.VT.Tests;

// Regression tests for #149: ED 2 (CSI 2 J) must erase the screen only — scrollback is
// preserved. ED 3 (CSI 3 J, xterm "Erase Saved Lines") clears the scrollback only.
// Previously both modes wiped the entire scrollback, so every `clear` (which ConPTY and
// ncurses emit as ED 2) silently destroyed the user's history.
public class EraseInDisplayTests
{
    private const int Cols = 20;
    private const int Rows = 5;

    private static (TerminalBuffer buffer, AnsiParser parser) CreateFilledTerminal(int lines = 12)
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < lines; i++)
        {
            parser.Process($"line{i}\r\n");
        }
        return (buffer, parser);
    }

    private static string ReadAbsoluteRow(TerminalBuffer buffer, int absRow)
    {
        var sb = new StringBuilder();
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < Cols; col++)
            {
                sb.Append(buffer.GetCellAbsolute(col, absRow).Character);
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        return sb.ToString().TrimEnd('\0', ' ');
    }

    private static string ReadViewportRow(TerminalBuffer buffer, int viewportRow)
        => ReadAbsoluteRow(buffer, buffer.Scrollback.Count + viewportRow);

    [Fact]
    public void Ed2_ErasesScreen_PreservesScrollback()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackBefore = buffer.Scrollback.Count;
        Assert.True(scrollbackBefore > 0, "test setup must overflow the viewport into scrollback");

        parser.Process("\x1b[2J");

        Assert.Equal(scrollbackBefore, buffer.Scrollback.Count);
        // Oldest line is still retrievable from scrollback.
        Assert.Equal("line0", ReadAbsoluteRow(buffer, 0));
        // Viewport is blank.
        for (int r = 0; r < Rows; r++)
        {
            Assert.Equal(string.Empty, ReadViewportRow(buffer, r));
        }
    }

    [Fact]
    public void Ed2_DoesNotMoveCursor()
    {
        var (buffer, parser) = CreateFilledTerminal();
        parser.Process("\x1b[3;4H"); // park cursor at row 3, col 4 (1-based)

        parser.Process("\x1b[2J");

        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(3, buffer.CursorCol);
    }

    [Fact]
    public void Ed3_ClearsScrollback_PreservesScreen()
    {
        var (buffer, parser) = CreateFilledTerminal();
        Assert.True(buffer.Scrollback.Count > 0);
        string topViewportRowBefore = ReadViewportRow(buffer, 0);
        Assert.NotEqual(string.Empty, topViewportRowBefore);

        parser.Process("\x1b[3J");

        Assert.Equal(0, buffer.Scrollback.Count);
        // Screen content is untouched (absolute index shifts because scrollback is gone).
        Assert.Equal(topViewportRowBefore, ReadViewportRow(buffer, 0));
    }

    [Fact]
    public void ClearCommandSequence_Ed2ThenEd3_ClearsScreenAndScrollback()
    {
        var (buffer, parser) = CreateFilledTerminal();

        // What `clear` emits with the E3 capability: home, ED 2, ED 3.
        parser.Process("\x1b[H\x1b[2J\x1b[3J");

        Assert.Equal(0, buffer.Scrollback.Count);
        for (int r = 0; r < Rows; r++)
        {
            Assert.Equal(string.Empty, ReadViewportRow(buffer, r));
        }
    }
}
