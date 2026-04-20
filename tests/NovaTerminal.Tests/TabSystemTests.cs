using System.Linq;
using NovaTerminal.Core;

namespace NovaTerminal.Tests;

public sealed class TabSystemTests
{
    [Fact]
    public void Ht_UsesDefaultEightColumnStops()
    {
        var buffer = new TerminalBuffer(32, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\t");

        Assert.Equal(8, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void Cht_AdvancesAcrossDefaultTabStops()
    {
        var buffer = new TerminalBuffer(32, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[2I");

        Assert.Equal(16, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void Cbt_MovesBackToPreviousDefaultTabStops()
    {
        var buffer = new TerminalBuffer(32, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[3I");
        parser.Process("\x1b[2Z");

        Assert.Equal(8, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void EscH_SetsCustomTabStopUsedByHt()
    {
        var buffer = new TerminalBuffer(32, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[6G");
        parser.Process("\x1bH");
        parser.Process("\r");
        parser.Process("\t");

        Assert.Equal(5, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void Csi0g_ClearsCurrentTabStop()
    {
        var buffer = new TerminalBuffer(32, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[6G");
        parser.Process("\x1bH");
        parser.Process("\x1b[0g");
        parser.Process("\r");
        parser.Process("\t");

        Assert.Equal(8, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void Csi3g_ClearsAllTabStops_AndHtClampsToRightMargin()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[3g");
        parser.Process("\t");

        Assert.Equal(19, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void Ht_WithoutFurtherStops_ClampsToRightMargin()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[18G");
        parser.Process("\t");

        Assert.Equal(19, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }
}
