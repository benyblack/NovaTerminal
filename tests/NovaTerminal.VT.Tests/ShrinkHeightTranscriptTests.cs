using System.Text;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// #404: shrinking a pane that holds content must not blank it.
/// </summary>
/// <remarks>
/// Splitting a pane halves its height, and the pane being split is nearly always taller than the
/// transcript it holds - a shell prompt sits part-way down a viewport whose lower rows are empty.
/// The height shrink used to take its rows off the top unconditionally, so a transcript occupying
/// the top half was pushed into scrollback in its entirety and the viewport kept the blank bottom
/// half: the pane came back with zero non-blank rows while empty rows were discarded for nothing.
///
/// These tests are about the width-unchanged path only. Reflow (a width change) rewraps and is a
/// different mechanism; the reported repro splits a pane vertically, which leaves width alone.
/// </remarks>
public class ShrinkHeightTranscriptTests
{
    [Fact]
    public void ShrinkHeight_WithBlankRowsBelowTheCursor_DiscardsThoseRatherThanTheTranscript()
    {
        // 21 written rows (20 lines plus a prompt) in a 50-row viewport: rows 21..49 are blank.
        var buffer = new TerminalBuffer(40, 50);
        for (int i = 1; i <= 20; i++) buffer.Write($"Line {i:D2}\r\n");
        buffer.Write("PROMPT>");

        Assert.Equal(0, buffer.Scrollback.Count);
        int promptRowBefore = buffer.CursorRow;

        // Halve the height, as splitting the pane does. 21 rows still fit in 25, so the shrink
        // has 29 blank rows to give up and no reason to touch the transcript at all.
        buffer.Resize(40, 25);

        Assert.Equal(0, buffer.Scrollback.Count);

        string viewport = ViewportText(buffer);
        Assert.Contains("Line 01", viewport);
        Assert.Contains("Line 20", viewport);
        Assert.Contains("PROMPT>", viewport);

        // The transcript did not move, so neither did the cursor.
        Assert.Equal(promptRowBefore, buffer.CursorRow);
    }

    [Fact]
    public void ShrinkHeight_WithMoreTranscriptThanFits_StillPushesTheOverflowToScrollback()
    {
        // The complement of the test above: when the blank rows below the cursor are not enough,
        // the remainder has to come off the top, and that eviction is correct.
        var buffer = new TerminalBuffer(40, 20);
        for (int i = 1; i <= 18; i++) buffer.Write($"Line {i:D2}\r\n");
        buffer.Write("PROMPT>");

        Assert.Equal(0, buffer.Scrollback.Count);

        // 19 occupied rows into 10: one blank row below the cursor to give up, so the other nine
        // rows must come off the top.
        buffer.Resize(40, 10);

        Assert.Equal(9, buffer.Scrollback.Count);

        string viewport = ViewportText(buffer);
        Assert.DoesNotContain("Line 01", viewport);
        Assert.Contains("Line 18", viewport);
        Assert.Contains("PROMPT>", viewport);

        // Scrolled-off rows are in history, not lost.
        Assert.Contains("Line 01", ScrollbackText(buffer));
    }

    [Fact]
    public void ShrinkHeight_NeverDiscardsARowBelowTheCursorThatHasContent()
    {
        // A blank row below the cursor is padding; a written one is not, whatever the cursor is
        // doing above it. Park the cursor at the top with content beneath it and shrink: the rows
        // that go must come off the top, because none of the trailing rows are droppable.
        var buffer = new TerminalBuffer(40, 20);
        for (int i = 1; i <= 12; i++) buffer.Write($"Line {i:D2}\r\n");
        buffer.Write("\x1b[1;1H"); // cursor home, content still below

        buffer.Resize(40, 10);

        string viewport = ViewportText(buffer);
        Assert.Contains("Line 12", viewport);
        Assert.Contains("Line 01", ScrollbackText(buffer) + viewport);
    }

    [Fact]
    public void ShrinkHeight_KeepsATrailingRowOfSpacesThatIsPainted()
    {
        // A row of spaces over a non-default background is a bar a TUI drew, not padding. It
        // has to survive a shrink like any other content - into scrollback if it must, but not
        // into nothing.
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        parser.Process("top line\r\n");
        parser.Process("\x1b[9;1H\x1b[44m    \x1b[0m");  // painted spaces on row 8
        parser.Process("\x1b[2;1H");                     // cursor back above them

        Assert.Equal(1, PaintedRowCount(buffer));

        buffer.Resize(20, 6);

        Assert.Equal(1, PaintedRowCount(buffer));
    }

    [Fact]
    public void ShrinkHeight_KeepsATrailingRowWhoseSpacesCarryAHyperlink()
    {
        // The same point for OSC 8: a link span routinely covers trailing spaces, and those
        // cells carry no signal of their own - the link lives in the row's side table.
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        parser.Process("top line\r\n");
        parser.Process("\x1b[9;1H\x1b]8;;https://example.com\x1b\\    \x1b]8;;\x1b\\");
        parser.Process("\x1b[2;1H");

        Assert.Equal(1, LinkedRowCount(buffer));

        buffer.Resize(20, 6);

        Assert.Equal(1, LinkedRowCount(buffer));
    }

    [Fact]
    public void ShrinkHeight_KeepsATrailingRowOfInverseSpaces()
    {
        // Inverse swaps foreground and background, so a run of inverse spaces paints a solid
        // bar - a status line, typically - while every cell still reports the default
        // background. Reading the background alone would call this row empty and drop it.
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        parser.Process("top line\r\n");
        parser.Process("\x1b[9;1H\x1b[7m    \x1b[0m");  // inverse spaces on row 8
        parser.Process("\x1b[2;1H");

        Assert.Equal(1, InverseRowCount(buffer));

        buffer.Resize(20, 6);

        Assert.Equal(1, InverseRowCount(buffer));
    }

    [Fact]
    public void ShrinkThenGrow_OnTheDetachedMainScreen_LeavesTheTranscriptWhereItWas()
    {
        // Reshape - the height resize that runs against the main screen while an alt-screen app
        // is up - deliberately does NOT shed padding first, because its grow half pops exactly
        // as many rows off scrollback as its shrink half pushes. Shedding instead of pushing
        // would leave the grow pulling rows the shrink never pushed, dragging real transcript
        // out of history and shifting everything below it down. This pins the pair: resize down
        // and back up with vim notionally open, and the main screen comes back as it went in.
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        for (int i = 1; i <= 20; i++) parser.Process($"Line {i:D2}\r\n");

        // Both halves of the hazard have to be present or this asserts nothing: scrollback
        // for the grow to pull from, and trailing padding for a padding-first shrink to
        // shed instead of pushing. Twenty lines into twelve rows makes the first; erasing
        // from row 4 down makes the second.
        parser.Process("\x1b[4;1H\x1b[J");
        Assert.True(buffer.Scrollback.Count > 0);

        // Captured before the switch: while the alt screen is active ViewportRows is the
        // alt buffer, so the main screen can only be read either side of it.
        string before = MainScreenText(buffer);
        parser.Process("\x1b[?1049h");   // vim et al.

        buffer.Resize(20, 6);
        buffer.Resize(20, 12);

        parser.Process("\x1b[?1049l");
        Assert.Equal(before, MainScreenText(buffer));
    }

    /// <summary>Rows anywhere in the buffer holding an inverse cell.</summary>
    private static int InverseRowCount(TerminalBuffer buffer)
    {
        int count = 0;

        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            var cells = buffer.Scrollback.GetRow(i);
            for (int c = 0; c < cells.Length; c++)
            {
                if (cells[c].IsInverse) { count++; break; }
            }
        }

        foreach (var row in buffer.ViewportRows)
        {
            for (int c = 0; c < row.Cells.Length; c++)
            {
                if (row.Cells[c].IsInverse) { count++; break; }
            }
        }

        return count;
    }

    /// <summary>Scrollback plus viewport, which for the main screen is the whole transcript.</summary>
    private static string MainScreenText(TerminalBuffer buffer)
        => ScrollbackText(buffer) + ViewportText(buffer);

    /// <summary>Rows anywhere in the buffer holding a cell with a non-default background.</summary>
    private static int PaintedRowCount(TerminalBuffer buffer)
    {
        int count = 0;

        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            var cells = buffer.Scrollback.GetRow(i);
            for (int c = 0; c < cells.Length; c++)
            {
                if (!cells[c].IsDefaultBackground) { count++; break; }
            }
        }

        foreach (var row in buffer.ViewportRows)
        {
            for (int c = 0; c < row.Cells.Length; c++)
            {
                if (!row.Cells[c].IsDefaultBackground) { count++; break; }
            }
        }

        return count;
    }

    /// <summary>Rows anywhere in the buffer carrying at least one OSC 8 hyperlink.</summary>
    private static int LinkedRowCount(TerminalBuffer buffer)
    {
        int count = 0;

        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            if (buffer.Scrollback.GetHyperlinkMap(i) is { Count: > 0 }) count++;
        }

        foreach (var row in buffer.ViewportRows)
        {
            if (row.GetHyperlinkMap() is { Count: > 0 }) count++;
        }

        return count;
    }

    private static string ViewportText(TerminalBuffer buffer)
    {
        var sb = new StringBuilder();
        foreach (var row in buffer.ViewportRows)
        {
            sb.Append(TextFromCells(row.Cells));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string ScrollbackText(TerminalBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            sb.Append(TextFromCells(buffer.Scrollback.GetRow(i)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string TextFromCells(ReadOnlySpan<TerminalCell> cells)
    {
        var chars = new char[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            char c = cells[i].Character;
            chars[i] = c == '\0' ? ' ' : c;
        }
        return new string(chars).TrimEnd();
    }
}
