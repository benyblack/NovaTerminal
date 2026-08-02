namespace NovaTerminal.VT.Tests;

// Issue #267: DECRQM (CSI Ps $ p / CSI ? Ps $ p) replies with DECRPM (CSI [?] Ps ; Pm $ y),
// reporting the live state of a tracked mode: Pm = 1 (set), 2 (reset), 0 (not recognized).
// Apps probe modes like ?2026 (synchronized output) via DECRQM before relying on them, instead
// of falling back to flickery full redraws when the query goes unanswered.
public class DecrqmTests
{
    private static (TerminalBuffer buffer, AnsiParser parser, List<string> responses) CreateTerminal()
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 8);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static string QueryPrivate(AnsiParser parser, List<string> responses, int mode)
    {
        responses.Clear();
        parser.Process($"\u001b[?{mode}$p");
        Assert.Single(responses);
        return responses[0];
    }

    private static string QueryAnsi(AnsiParser parser, List<string> responses, int mode)
    {
        responses.Clear();
        parser.Process($"\u001b[{mode}$p");
        Assert.Single(responses);
        return responses[0];
    }

    [Theory]
    [InlineData(1)]    // DECCKM - Application Cursor Keys
    [InlineData(6)]    // DECOM - Origin Mode
    [InlineData(7)]    // DECAWM - Auto Wrap Mode
    [InlineData(25)]   // DECTCEM - Text Cursor Enable Mode
    [InlineData(47)]   // Alternate screen (legacy)
    [InlineData(1000)] // X10 mouse reporting
    [InlineData(1002)] // Button-event mouse tracking
    [InlineData(1003)] // Any-event mouse tracking
    [InlineData(1004)] // Focus in/out reporting
    [InlineData(1006)] // SGR extended mouse mode
    [InlineData(1047)] // Alternate screen
    [InlineData(1049)] // Alternate screen + save cursor
    [InlineData(2004)] // Bracketed paste mode
    [InlineData(2026)] // Synchronized output (batch rendering)
    public void TrackedPrivateMode_ReportsSetThenReset(int mode)
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process($"\u001b[?{mode}h");
        Assert.Equal($"\u001b[?{mode};1$y", QueryPrivate(parser, responses, mode));

        parser.Process($"\u001b[?{mode}l");
        Assert.Equal($"\u001b[?{mode};2$y", QueryPrivate(parser, responses, mode));
    }

    [Fact]
    public void UnknownPrivateMode_ReportsNotRecognized()
    {
        var (_, parser, responses) = CreateTerminal();

        Assert.Equal("\u001b[?12345;0$y", QueryPrivate(parser, responses, 12345));
    }

    [Fact]
    public void UnknownAnsiMode_ReportsNotRecognized()
    {
        var (_, parser, responses) = CreateTerminal();

        Assert.Equal("\u001b[99;0$y", QueryAnsi(parser, responses, 99));
    }

    [Theory]
    [InlineData(4)]  // IRM - Insert Replacement Mode
    [InlineData(20)] // LNM - Line Feed New Line Mode
    public void TrackedAnsiMode_ReportsSetThenReset(int mode)
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process($"\u001b[{mode}h");
        Assert.Equal($"\u001b[{mode};1$y", QueryAnsi(parser, responses, mode));

        parser.Process($"\u001b[{mode}l");
        Assert.Equal($"\u001b[{mode};2$y", QueryAnsi(parser, responses, mode));
    }

    [Fact]
    public void Srm_ReportsModeFlagNotEchoInterpretation()
    {
        // SRM set (h) turns local echo OFF; SRM reset (l) turns it back ON. DECRQM must report
        // the mode flag's own set/reset state, not the echo interpretation directly - "set"
        // here corresponds to IsEchoEnabled == false.
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\u001b[12h");
        Assert.False(buffer.Modes.IsEchoEnabled);
        Assert.Equal("\u001b[12;1$y", QueryAnsi(parser, responses, 12));

        parser.Process("\u001b[12l");
        Assert.True(buffer.Modes.IsEchoEnabled);
        Assert.Equal("\u001b[12;2$y", QueryAnsi(parser, responses, 12));
    }

    [Fact]
    public void Sync2026_TracksActualBeginEndSyncState()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\u001b[?2026h");
        Assert.True(buffer.IsSynchronizedOutput);
        Assert.Equal("\u001b[?2026;1$y", QueryPrivate(parser, responses, 2026));

        parser.Process("\u001b[?2026l");
        Assert.False(buffer.IsSynchronizedOutput);
        Assert.Equal("\u001b[?2026;2$y", QueryPrivate(parser, responses, 2026));
    }

    [Fact]
    public void DecstrFinalByte_WithBangIntermediate_ProducesNoReply()
    {
        // CSI ! p is DECSTR (soft reset), not DECRQM. Dispatch must key on the '$' intermediate
        // exactly so this never gets mistaken for a mode query.
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\u001b[!p");

        Assert.Empty(responses);
    }

    [Fact]
    public void BareFinalByteP_ProducesNoReply()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\u001b[p");

        Assert.Empty(responses);
    }
}
