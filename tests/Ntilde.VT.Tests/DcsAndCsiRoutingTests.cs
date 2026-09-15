using System.Text;

namespace NovaTerminal.VT.Tests;

// Regression tests for #169: DCS routing (DECRQSS/XTGETTCAP vs Sixel) and
// C0-control handling inside CSI sequences.
public class DcsAndCsiRoutingTests
{
    private static (TerminalBuffer buffer, AnsiParser parser, List<string> responses) CreateTerminal()
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 5);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static string ReadRow(TerminalBuffer buffer, int viewRow, int cols = 40)
    {
        var sb = new StringBuilder();
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < cols; col++)
            {
                sb.Append(buffer.GetGrapheme(col, viewRow));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        return sb.ToString().TrimEnd('\0', ' ');
    }

    [Fact]
    public void Decrqss_GetsInvalidRequestResponse_NotSixel()
    {
        var (_, parser, responses) = CreateTerminal();

        // vim's SGR query: DCS $ q m ST — previously misrouted to the Sixel decoder
        // (it contains 'q') and never answered, so vim blocked on its timeout.
        parser.Process("\x1bP$qm\x1b\\");

        Assert.Single(responses);
        Assert.Equal("\x1bP0$r\x1b\\", responses[0]);
    }

    [Fact]
    public void Xtgettcap_GetsFailureResponse()
    {
        var (_, parser, responses) = CreateTerminal();

        // XTGETTCAP for "TN" (hex 544e): DCS + q 544e ST
        parser.Process("\x1bP+q544e\x1b\\");

        Assert.Single(responses);
        Assert.Equal("\x1bP0+r\x1b\\", responses[0]);
    }

    [Fact]
    public void NonSixelDcs_WithLetterQ_IsNotFedToSixelDecoder()
    {
        var (_, parser, _) = CreateTerminal();

        // An unknown DCS whose payload merely contains 'q' — must not throw or
        // produce sixel artifacts. (No decoder attached; just must not misroute.)
        var ex = Record.Exception(() => parser.Process("\x1bPzzzqzzz\x1b\\"));

        Assert.Null(ex);
    }

    [Fact]
    public void C0Controls_InsideCsi_AreExecuted_AndSequenceContinues()
    {
        var (buffer, parser, _) = CreateTerminal();

        parser.Process("AB");
        // SGR split by a CR: ESC[3 <CR> 2m — per ECMA-48 the CR executes and the
        // sequence continues as CSI 32 m. Previously both were dropped.
        parser.Process("\x1b[3\r2m");
        parser.Process("X");

        Assert.Equal(0, buffer.CursorRow);
        // CR executed: cursor returned to col 0, so X overwrote A.
        Assert.Equal("XB", ReadRow(buffer, 0));
        // Sequence continued: SGR 32 (green fg) applied to X.
        buffer.Lock.EnterReadLock();
        try
        {
            var cell = buffer.GetCellAbsolute(0, buffer.Scrollback.Count);
            Assert.Equal(2, cell.FgIndex); // palette index 2 = green
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData('\x18')] // CAN
    [InlineData('\x1a')] // SUB
    public void CanAndSub_AbortCsiSequence(char abortChar)
    {
        var (buffer, parser, _) = CreateTerminal();

        parser.Process($"\x1b[3{abortChar}1m");
        parser.Process("X");

        // The aborted SGR must not apply; the trailing "1m" is plain text per xterm
        // (it was never part of a sequence).
        Assert.Contains("1mX", ReadRow(buffer, 0));
    }

}
