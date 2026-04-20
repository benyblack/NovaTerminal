using NovaTerminal.Core;

namespace NovaTerminal.Tests;

public class CursorPositioningCompletionTests
{
    private static AnsiParser CreateParser(TerminalBuffer buffer) => new(buffer);

    [Theory]
    [InlineData("\x1b[H", 0, 0)]
    [InlineData("\x1b[f", 0, 0)]
    [InlineData("\x1b[0;0H", 0, 0)]
    [InlineData("\x1b[5;H", 4, 0)]
    [InlineData("\x1b[;7f", 0, 6)]
    [InlineData("\x1b[0;9H", 0, 8)]
    public void CupAndHvp_DefaultMissingAndZeroParametersToOne(string sequence, int expectedRow, int expectedCol)
    {
        var buffer = new TerminalBuffer(12, 10);
        var parser = CreateParser(buffer);

        parser.Process("\x1b[8;10H");
        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(expectedCol, buffer.CursorCol);
    }

    [Fact]
    public void CupAndVpa_OriginMode_UseScrollRegionHomeAndClampToMargins()
    {
        var buffer = new TerminalBuffer(12, 12);
        var parser = CreateParser(buffer);

        parser.Process("\x1b[5;10r");
        parser.Process("\x1b[?6h");

        parser.Process("\x1b[0;0H");
        Assert.Equal(4, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);

        parser.Process("\x1b[999;1f");
        Assert.Equal(9, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);

        parser.Process("\x1b[0d");
        Assert.Equal(4, buffer.CursorRow);

        parser.Process("\x1b[999d");
        Assert.Equal(9, buffer.CursorRow);
    }

    [Theory]
    [InlineData("\x1b[G", 0)]
    [InlineData("\x1b[0G", 0)]
    [InlineData("\x1b[7G", 6)]
    [InlineData("\x1b[7`", 6)]
    [InlineData("\x1b[999`", 11)]
    public void ChaAndHpa_DefaultZeroAndLargeParametersClampConsistently(string sequence, int expectedCol)
    {
        var buffer = new TerminalBuffer(12, 6);
        var parser = CreateParser(buffer);

        parser.Process("\x1b[4;4H");
        parser.Process(sequence);

        Assert.Equal(3, buffer.CursorRow);
        Assert.Equal(expectedCol, buffer.CursorCol);
    }

    [Fact]
    public void Hpr_DefaultZeroAndLargeParametersMoveRightAndClamp()
    {
        var buffer = new TerminalBuffer(12, 6);
        var parser = CreateParser(buffer);

        parser.Process("\x1b[2;2H");
        parser.Process("\x1b[a");
        Assert.Equal(2, buffer.CursorCol);

        parser.Process("\x1b[0a");
        Assert.Equal(3, buffer.CursorCol);

        parser.Process("\x1b[3a");
        Assert.Equal(6, buffer.CursorCol);

        parser.Process("\x1b[999a");
        Assert.Equal(11, buffer.CursorCol);
    }

    [Fact]
    public void Vpr_ClampsToScrollRegionWhenCursorStartsInsideMargins()
    {
        var buffer = new TerminalBuffer(12, 8);
        var parser = CreateParser(buffer);

        parser.Process("\x1b[3;6r");
        parser.Process("\x1b[?6h");

        parser.Process("\x1b[e");
        Assert.Equal(3, buffer.CursorRow);

        parser.Process("\x1b[0e");
        Assert.Equal(4, buffer.CursorRow);

        parser.Process("\x1b[999e");
        Assert.Equal(5, buffer.CursorRow);
    }

    [Fact]
    public void Vpr_UsesViewportBoundsWhenCursorStartsOutsideScrollRegion()
    {
        var buffer = new TerminalBuffer(12, 8);
        var parser = CreateParser(buffer);

        parser.Process("\x1b[3;6r");
        parser.Process("\x1b[1;1H");
        parser.Process("\x1b[999e");

        Assert.Equal(7, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }
}
