using System.Text;

namespace NovaTerminal.VT.Tests;

public class CursorLinePositioningTests
{
    [Theory]
    [InlineData("\x1b[E", 4)]
    [InlineData("\x1b[0E", 4)]
    [InlineData("\x1b[2E", 5)]
    [InlineData("\x1b[999E", 7)]
    public void Cnl_MovesDownAndResetsColumn(string sequence, int expectedRow)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }

    [Theory]
    [InlineData("\x1b[F", 2)]
    [InlineData("\x1b[0F", 2)]
    [InlineData("\x1b[2F", 1)]
    [InlineData("\x1b[999F", 0)]
    public void Cpl_MovesUpAndResetsColumn(string sequence, int expectedRow)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }

    [Theory]
    [InlineData("\x1b[999E", 5)]
    [InlineData("\x1b[999F", 2)]
    public void CnlAndCpl_ClampToMarginsWhenCursorStartsInsideScrollRegion(
        string sequence,
        int expectedRow)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[3;6r");
        parser.Process("\x1b[5;6H");

        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }

    [Theory]
    [InlineData("\x1b[1;6H", "\x1b[999E", 7)]
    [InlineData("\x1b[8;6H", "\x1b[999F", 0)]
    public void CnlAndCpl_ClampToViewportWhenCursorStartsOutsideScrollRegion(
        string startPosition,
        string sequence,
        int expectedRow)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[3;6r");
        parser.Process(startPosition);

        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }

    [Fact]
    public void DotnetTerminalLoggerRefresh_ReusesProgressRow()
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("Build started\r\n");

        parser.Process("\x1b[1F\r\n\x1b[K\x1b[31G(0.4s)");
        parser.Process("\x1b[1F\r\n\x1b[K\x1b[31G(0.5s)");

        Assert.Equal(1, buffer.CursorRow);
        Assert.EndsWith("(0.5s)", GetRowText(buffer, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("(0.4s)", GetRowText(buffer, 1), StringComparison.Ordinal);
        Assert.Equal(string.Empty, GetRowText(buffer, 2));
        Assert.Equal(string.Empty, GetRowText(buffer, 3));
    }

    private static string GetRowText(TerminalBuffer buffer, int row)
    {
        var text = new StringBuilder(buffer.Cols);
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < buffer.Cols; col++)
            {
                text.Append(buffer.GetGrapheme(col, row));
            }
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }

        return text.ToString().TrimEnd();
    }
}
