using System;
using System.Text;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// Edge-case coverage for #123. Reflow/resize basics are tested elsewhere; these target the
// historically bug-prone cases that had no tests: wide chars at the wrap boundary, resize with
// scrollback present, degenerate widths, alt-screen width changes, scrollback trimming on rewrap,
// and the post-resize consistency invariant (the failsafe guarantee).
public class ReflowEdgeCaseTests
{
    [Fact]
    public void WideChar_AtWrapBoundary_IsNotSplitAcrossRows()
    {
        // 6 cols: "AB" (cols 0,1) then 世 (2,3) 界 (4,5) exactly fills the row.
        var buffer = new TerminalBuffer(6, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("AB世界CD");

        // Shrink to 5 cols: 世 occupies 2,3; 界 can no longer fit at col 4 (needs 4,5) and must
        // wrap rather than be split with an orphaned continuation cell.
        buffer.Resize(5, 4);

        AssertNoWideCharSplitAtRowEnd(buffer);
        AssertInvariants(buffer, 5, 4);

        // Grow back; both wide glyphs must survive (a wide cell is followed by a continuation
        // cell, so the two glyphs never read as adjacent "世界" — assert each is present).
        buffer.Resize(8, 4);
        AssertNoWideCharSplitAtRowEnd(buffer);
        string text = CombinedText(buffer);
        Assert.Contains("世", text, StringComparison.Ordinal);
        Assert.Contains("界", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_WithScrollbackPresent_PreservesContent()
    {
        var buffer = new TerminalBuffer(20, 4);
        for (int i = 1; i <= 20; i++)
        {
            buffer.Write($"L{i:D2}-abcdefgh\r\n");
        }

        // Scrollback must exist for this to be meaningful.
        Assert.True(buffer.Scrollback.Count > 0, "expected scrollback to accumulate");

        string combinedBefore = CombinedText(buffer);
        Assert.Contains("L05-abcdefgh", combinedBefore, StringComparison.Ordinal);

        // Shrink then grow back to the original width: known lines survive the round trip.
        buffer.Resize(10, 4);
        AssertInvariants(buffer, 10, 4);
        buffer.Resize(20, 4);
        AssertInvariants(buffer, 20, 4);

        string combinedAfter = CombinedText(buffer);
        Assert.Contains("L05-abcdefgh", combinedAfter, StringComparison.Ordinal);
        Assert.Contains("L20-abcdefgh", combinedAfter, StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_ToSingleColumn_DoesNotCorrupt_AndRoundTrips()
    {
        var buffer = new TerminalBuffer(10, 4);
        buffer.Write("ABCDEFGH");

        buffer.Resize(1, 4);
        AssertInvariants(buffer, 1, 4);

        buffer.Resize(10, 4);
        AssertInvariants(buffer, 10, 4);
        Assert.Contains("ABCDEFGH", CombinedText(buffer), StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_ToDegenerateWidth_LeavesBufferUsable()
    {
        var buffer = new TerminalBuffer(10, 4);
        buffer.Write("hello");

        // Zero / negative widths are invalid; resizing to them must not corrupt the buffer or
        // throw, and a subsequent valid resize must produce a consistent buffer.
        buffer.Resize(0, 4);
        buffer.Resize(-5, 4);
        buffer.Resize(10, 4);

        AssertInvariants(buffer, 10, 4);
    }

    [Fact]
    public void AltScreen_WidthChange_ThenReturnToMain_PreservesMainContent()
    {
        var buffer = new TerminalBuffer(20, 5);
        buffer.Write("MAIN-LINE-CONTENT-HERE");

        buffer.SwitchToAltScreen();
        buffer.Write("ALT");
        Assert.True(buffer.IsAltScreenActive);

        // Width change while the alt screen is active.
        buffer.Resize(10, 5);
        AssertInvariants(buffer, 10, 5);

        buffer.SwitchToMainScreen();
        Assert.False(buffer.IsAltScreenActive);
        AssertInvariants(buffer, 10, 5);

        // The main screen content, rewrapped to the new width, must still be present.
        Assert.Contains("MAIN-LINE-CONTENT-HERE", CombinedText(buffer).Replace("\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_RewrapOverflowingScrollback_TrimsToBudget_AndKeepsRecentContent()
    {
        // Tight scrollback budget so narrowing (which multiplies wrapped rows) forces eviction.
        var buffer = new TerminalBuffer(80, 4) { MaxScrollbackBytes = 16 * 1024 };
        for (int i = 1; i <= 200; i++)
        {
            buffer.Write($"row{i:D3}-{new string('x', 60)}\r\n");
        }

        buffer.Resize(10, 4); // heavy rewrap → many more physical rows → eviction
        AssertInvariants(buffer, 10, 4);

        // Most recent content is retained; the buffer is not corrupted.
        Assert.Contains("row200", CombinedText(buffer), StringComparison.Ordinal);
    }

    // --- helpers ---

    private static void AssertInvariants(TerminalBuffer buffer, int expectedCols, int expectedRows)
    {
        Assert.Equal(expectedRows, buffer.Rows);
        Assert.Equal(expectedCols, buffer.Cols);
        Assert.Equal(expectedRows, buffer.ViewportRows.Count);
        foreach (var row in buffer.ViewportRows)
        {
            Assert.Equal(expectedCols, row.Cells.Length);
        }
        Assert.InRange(buffer.CursorRow, 0, expectedRows - 1);
        Assert.InRange(buffer.CursorCol, 0, expectedCols);
    }

    private static void AssertNoWideCharSplitAtRowEnd(TerminalBuffer buffer)
    {
        foreach (var row in buffer.ViewportRows)
        {
            var cells = row.Cells;
            int last = cells.Length - 1;
            // A wide cell must be followed by its continuation; it can never be the last cell.
            Assert.False(cells[last].IsWide, "a wide cell must not sit at the row's last column");
        }
    }

    private static string CombinedText(TerminalBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            sb.Append(TextFromCells(buffer.Scrollback.GetRow(i)));
            sb.Append('\n');
        }
        foreach (var row in buffer.ViewportRows)
        {
            sb.Append(TextFromCells(row.Cells));
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
