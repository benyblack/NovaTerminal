using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// The command-input window: <c>TerminalBuffer.IsAcceptingCommandInput</c>, moved by the parser on
/// <c>OSC 133;B</c> / <c>C</c> / <c>D</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <see cref="Osc133CommandStartMarkTests"/>'s mark, and it lives beside
/// it deliberately. Whether the cells between the mark and the cursor are a command line being
/// edited or the output of a command that already ran is a lifecycle fact carried only by the OSC
/// 133 stream, so a consumer that reads the mark without the window gets an answer it cannot trust.
/// </para>
/// <para>
/// It used to live on Command Assist's session context, written after a hop onto the pane's
/// serialized event dispatcher while the mark was written here, synchronously. Under a busy
/// dispatcher the two disagreed, the grid read was refused, and a submitted command was dropped
/// from history outright - silently and permanently (#448). Both halves now move in the same
/// statement block, which is what these tests pin.
/// </para>
/// </remarks>
public class Osc133CommandInputWindowTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";
    private const string CommandAccepted = "\x1b]133;C\x07";
    private const string CommandFinished = "\x1b]133;D;0\x07";

    private static (AnsiParser Parser, TerminalBuffer Buffer) Make(int cols = 40, int rows = 5)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer);

        // The window is written to the buffer by the parser; the mark is handed to a subscriber and
        // written by the host (TerminalPane's OnCommandStarted). Wiring it the way the App does is
        // what lets the "mark survives C, window does not" asymmetry be asserted here at all.
        parser.OnCommandStarted = mark => buffer.CommandStartMark = mark;
        return (parser, buffer);
    }

    [Fact]
    public void AFreshBuffer_IsNotAcceptingCommandInput()
    {
        var (_, buffer) = Make();

        Assert.False(buffer.IsAcceptingCommandInput);
    }

    [Fact]
    public void PromptEnd_OpensTheWindow()
    {
        var (parser, buffer) = Make();

        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);

        Assert.True(buffer.IsAcceptingCommandInput);
    }

    /// <summary>
    /// The window closes on <c>C</c> even though the mark does not. That asymmetry is the point: at
    /// <c>C</c> the input line is still painted and the mark still describes it, so the grid reader
    /// would answer - and its answer is about to become this command's output.
    /// </summary>
    [Fact]
    public void CommandAccepted_ClosesTheWindow_ButKeepsTheMark()
    {
        var (parser, buffer) = Make();
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd + "git status");

        parser.Process(CommandAccepted);

        Assert.False(buffer.IsAcceptingCommandInput);
        Assert.NotNull(buffer.CommandStartMark);
    }

    /// <summary>A shell can reach <c>D</c> with no intervening <c>C</c>.</summary>
    [Fact]
    public void CommandFinished_ClosesTheWindow_WithNoInterveningAccept()
    {
        var (parser, buffer) = Make();
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd + "git status");

        parser.Process(CommandFinished);

        Assert.False(buffer.IsAcceptingCommandInput);
    }

    [Fact]
    public void TheNextPromptReopensTheWindow()
    {
        var (parser, buffer) = Make();
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd + "git status");
        parser.Process(CommandAccepted);
        parser.Process(CommandFinished);

        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);

        Assert.True(buffer.IsAcceptingCommandInput);
    }

    /// <summary>
    /// Prompt frameworks repaint constantly and every repaint carries <c>B</c>, so re-opening an
    /// open window has to be a no-op rather than anything stateful.
    /// </summary>
    [Fact]
    public void ARepaintedPrompt_LeavesTheWindowOpen()
    {
        var (parser, buffer) = Make();

        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);

        Assert.True(buffer.IsAcceptingCommandInput);
    }

    /// <summary>
    /// Entering the alt screen closes it: the prompt that emitted <c>B</c> is not on the screen the
    /// user is looking at any more, and a refresh would otherwise read a TUI's grid as a command
    /// line.
    /// </summary>
    [Fact]
    public void EnteringTheAltScreen_ClosesTheWindow()
    {
        var (parser, buffer) = Make();
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);

        parser.Process("\x1b[?1049h");

        Assert.False(buffer.IsAcceptingCommandInput);
    }

    /// <summary>
    /// A full-screen TUI drawing its own prompt may legally emit <c>133;B</c>, and that must not pry
    /// a window open onto the TUI's own grid. Refused at the source so the invariant holds whichever
    /// order the two facts arrive in.
    /// </summary>
    [Fact]
    public void OnTheAltScreen_APromptMarkDoesNotOpenTheWindow()
    {
        var (parser, buffer) = Make();
        parser.Process("\x1b[?1049h");

        parser.Process(PromptStart + "tui> " + PromptEnd);

        Assert.False(buffer.IsAcceptingCommandInput);
    }

    /// <summary>
    /// Leaving the alt screen does not reopen it. The shell repaints its prompt on teardown and that
    /// repaint re-emits <c>B</c>, so it reopens on evidence rather than on assumption.
    /// </summary>
    [Fact]
    public void LeavingTheAltScreen_DoesNotReopenTheWindowOnItsOwn()
    {
        var (parser, buffer) = Make();
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);
        parser.Process("\x1b[?1049h");

        parser.Process("\x1b[?1049l");

        Assert.False(buffer.IsAcceptingCommandInput);
    }

    /// <summary>A replaced session must not inherit the old one's open window.</summary>
    [Fact]
    public void ClearTrackedShellMarks_ClosesTheWindow()
    {
        var (parser, buffer) = Make();
        parser.Process(PromptStart + "user@host:~$ " + PromptEnd);

        buffer.ClearTrackedShellMarks();

        Assert.False(buffer.IsAcceptingCommandInput);
    }
}
