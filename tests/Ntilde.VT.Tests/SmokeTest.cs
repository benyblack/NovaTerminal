using System.Text;

namespace NovaTerminal.VT.Tests;

public class SmokeTest
{
    [Fact]
    public void Parser_writes_plain_ASCII_to_buffer()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 1);
        var parser = new AnsiParser(buffer);

        parser.Process("hello");

        var sb = new StringBuilder();
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < 5; col++)
            {
                sb.Append(buffer.GetGrapheme(col, viewRow: 0));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }

        Assert.Equal("hello", sb.ToString());
    }

    [Fact]
    public void Buffer_reports_visual_cursor_row_zero_on_single_row_terminal()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 1);
        var parser = new AnsiParser(buffer);

        parser.Process("hi");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(0, buffer.GetVisualCursorRow());
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
