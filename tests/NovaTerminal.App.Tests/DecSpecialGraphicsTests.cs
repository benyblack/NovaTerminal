using System.Linq;
using NovaTerminal.Core;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests;

public sealed class DecSpecialGraphicsTests
{
    private const string SO = ""; // shift to G1
    private const string SI = ""; // shift to G0

    [Fact]
    public void DesignateG0_DecGraphics_MapsBoxDrawingLetters()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        // ESC ( 0 designates G0 as DEC Special Graphics; GL defaults to G0.
        parser.Process("(0lqknxmjtuvw(B");

        Assert.Equal("┌─┐┼│└┘├┤┴┬", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void DesignateG0_DecGraphics_DoesNotAffectAsciiAfterRestore()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("(0q(Bq");

        Assert.Equal("─q", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void ShiftOutIn_SwitchesActiveCharsetBetweenG0AndG1()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        // G0 stays ASCII (default), G1 = DEC graphics. SO shifts to G1, SI back to G0.
        // This is the pattern mc/ncurses use via terminfo `smacs`/`rmacs`.
        parser.Process(")0A" + SO + "qB" + SI + "b");

        Assert.Equal("A─Bb", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void DesignateG1_AsAscii_IgnoresSoShifts()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process(")B" + SO + "q" + SI + "q");

        Assert.Equal("qq", GetVisiblePlainText(buffer).Trim());
    }

    private static string GetVisiblePlainText(TerminalBuffer buffer)
    {
        return string.Join("\n", buffer.ViewportRows.Select(GetRowText)).TrimEnd();
    }

    private static string GetRowText(TerminalRow row)
    {
        var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }
}
