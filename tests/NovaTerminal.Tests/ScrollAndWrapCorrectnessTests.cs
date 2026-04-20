using System.Linq;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests;

public sealed class ScrollAndWrapCorrectnessTests
{
    [Fact]
    public void Decstbm_InvalidRegion_IsIgnored()
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[2;4r");
        Assert.Equal(1, buffer.ScrollTop);
        Assert.Equal(3, buffer.ScrollBottom);

        parser.Process("\x1b[4;2r");

        Assert.Equal(1, buffer.ScrollTop);
        Assert.Equal(3, buffer.ScrollBottom);
    }

    [Fact]
    public void Ind_AtBottomMargin_ScrollsOnlyInsideRegion()
    {
        var buffer = new TerminalBuffer(2, 5);
        var parser = new AnsiParser(buffer);

        parser.Process("A\r\nB\r\nC\r\nD\r\nE");
        parser.Process("\x1b[2;4r");
        buffer.SetCursorPosition(0, 3);

        parser.Process("\u001bD");

        Assert.Equal("A", GetRowText(buffer, 0));
        Assert.Equal("C", GetRowText(buffer, 1));
        Assert.Equal("D", GetRowText(buffer, 2));
        Assert.Equal(string.Empty, GetRowText(buffer, 3));
        Assert.Equal("E", GetRowText(buffer, 4));
        Assert.Equal(3, buffer.CursorRow);
    }

    [Fact]
    public void Ri_AtTopMargin_ScrollsOnlyInsideRegion()
    {
        var buffer = new TerminalBuffer(2, 5);
        var parser = new AnsiParser(buffer);

        parser.Process("A\r\nB\r\nC\r\nD\r\nE");
        parser.Process("\x1b[2;4r");
        buffer.SetCursorPosition(0, 1);

        parser.Process("\x1bM");

        Assert.Equal("A", GetRowText(buffer, 0));
        Assert.Equal(string.Empty, GetRowText(buffer, 1));
        Assert.Equal("B", GetRowText(buffer, 2));
        Assert.Equal("C", GetRowText(buffer, 3));
        Assert.Equal("E", GetRowText(buffer, 4));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void PendingWrap_AtBottomMargin_ScrollsWithinRegion_OnNextPrintable()
    {
        var buffer = new TerminalBuffer(3, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("aaa\r\nbbb\r\nccc\r\nddd");
        parser.Process("\x1b[2;4r");
        buffer.SetCursorPosition(2, 3);
        buffer.WriteChar('X');

        Assert.True(buffer.IsPendingWrap);

        buffer.WriteChar('Y');

        Assert.Equal("aaa", GetRowText(buffer, 0));
        Assert.Equal("ccc", GetRowText(buffer, 1));
        Assert.Equal("ddX", GetRowText(buffer, 2));
        Assert.Equal("Y", GetRowText(buffer, 3));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Equal(3, buffer.CursorRow);
    }

    [Fact]
    public void CombiningMark_AfterLastColumnPendingWrap_AttachesToLastCell()
    {
        var buffer = new TerminalBuffer(5, 3);

        buffer.SetCursorPosition(4, 0);
        buffer.WriteChar('e');

        Assert.True(buffer.IsPendingWrap);

        buffer.WriteContent("\u0301");

        Assert.Equal("e\u0301", GetGraphemeSafe(buffer, 4, 0));
        Assert.Equal(" ", GetGraphemeSafe(buffer, 3, 0));
        Assert.True(buffer.IsPendingWrap);
        Assert.Equal(4, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void CombiningMark_AfterWideGlyphAtRightEdgePendingWrap_AttachesToWideBaseCell()
    {
        var buffer = new TerminalBuffer(5, 3);

        buffer.SetCursorPosition(3, 0);
        buffer.WriteContent("あ");

        Assert.True(buffer.IsPendingWrap);

        buffer.WriteContent("\u0301");

        Assert.Equal("あ\u0301", GetGraphemeSafe(buffer, 3, 0));
        Assert.Equal(" ", GetGraphemeSafe(buffer, 4, 0));
        Assert.True(buffer.ViewportRows[0].Cells[4].IsWideContinuation);
        Assert.True(buffer.IsPendingWrap);
        Assert.Equal(4, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void WideGlyph_AtRightEdgeOfBottomMargin_WrapsAndScrollsInsideRegion()
    {
        var buffer = new TerminalBuffer(5, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("aa\r\nbb\r\ncc\r\ndd");
        parser.Process("\x1b[2;4r");
        buffer.SetCursorPosition(4, 3);

        buffer.WriteContent("あ");

        Assert.Equal("aa", GetRowText(buffer, 0));
        Assert.Equal("cc", GetRowText(buffer, 1));
        Assert.Equal("dd", GetRowText(buffer, 2));
        Assert.Equal("あ", GetRowText(buffer, 3));
        Assert.Equal(2, buffer.CursorCol);
        Assert.Equal(3, buffer.CursorRow);
    }

    [Fact]
    public void Decstbm_ZeroOrOutOfBoundsInvalidRegion_IsIgnoredAndPreservesExistingRegion()
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[2;4r");
        Assert.Equal(1, buffer.ScrollTop);
        Assert.Equal(3, buffer.ScrollBottom);

        parser.Process("\x1b[0;1r");
        Assert.Equal(1, buffer.ScrollTop);
        Assert.Equal(3, buffer.ScrollBottom);

        parser.Process("\x1b[99;100r");
        Assert.Equal(1, buffer.ScrollTop);
        Assert.Equal(3, buffer.ScrollBottom);
    }

    private static string GetRowText(TerminalBuffer buffer, int row)
    {
        return new string(buffer.ViewportRows[row].Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()).TrimEnd();
    }

    private static string GetGraphemeSafe(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return buffer.GetGrapheme(col, row);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
