using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// The Phase 1b seam: <c>TerminalPane.TryGetGridCommandLine</c> combines the newest
/// <c>OSC 133;B</c> mark with the pane's buffer and hands both to
/// <see cref="GridQueryReader"/>. Extraction semantics are covered exhaustively in
/// <c>NovaTerminal.VT.Tests.GridQueryReaderTests</c>; what is pinned here is the wiring —
/// that the pane keeps the mark at all, and keeps the <i>newest</i> one.
/// </summary>
public class PaneGridCommandLineTests
{
    private const string PromptEnd = "\x1b]133;B\x07";
    private const string CommandExecuted = "\x1b]133;C\x07";
    private const string CommandFinished = "\x1b]133;D;0\x07";

    [AvaloniaFact]
    public void WithoutAMark_ThereIsNoGridCommandLine()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("$ git status");

        Assert.False(pane.TryGetGridCommandLine(out _));
    }

    [AvaloniaFact]
    public void AfterAPromptMark_TheLiveCommandLineIsReadFromTheGrid()
    {
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("\x1b]133;A\x07user@host:~$ ");
        pane.Parser!.Process(PromptEnd);
        pane.Parser!.Process("git status");

        Assert.True(pane.TryGetGridCommandLine(out GridCommandLine line));
        Assert.Equal("git status", line.Text);
        Assert.Equal(10, line.CursorOffset);
    }

    [AvaloniaFact]
    public void APromptRepaintReplacesTheMark()
    {
        // B rides inside the prompt string, so every repaint re-emits it. Holding the first
        // mark would leave the reader anchored to a prompt that is no longer on screen.
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("\x1b]133;A\x07$ " + PromptEnd + "stale");
        pane.Parser!.Process("\r\x1b[K");
        pane.Parser!.Process("\x1b]133;A\x07nova> " + PromptEnd + "ls -la");

        Assert.True(pane.TryGetGridCommandLine(out GridCommandLine line));
        Assert.Equal("ls -la", line.Text);
    }

    [AvaloniaFact]
    public void TheMarkSurvivesCommandExecutionButNotCommandCompletion()
    {
        // C (executed) must not drop the mark: it fires the instant the user submits, while the
        // input line is still on screen and still exactly what the mark describes. D (finished)
        // must, or the span from the mark to the cursor is the command's *output* -- and it
        // stays dropped until the next prompt re-emits B.
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("\x1b]133;A\x07$ " + PromptEnd + "git status");

        pane.Parser!.Process(CommandExecuted);
        Assert.True(pane.TryGetGridCommandLine(out GridCommandLine submitted));
        Assert.Equal("git status", submitted.Text);

        pane.Parser!.Process("\r\n On branch main\r\n");
        pane.Parser!.Process(CommandFinished);

        Assert.False(pane.TryGetGridCommandLine(out _));

        pane.Parser!.Process("\x1b]133;A\x07$ " + PromptEnd + "ls");

        Assert.True(pane.TryGetGridCommandLine(out GridCommandLine next));
        Assert.Equal("ls", next.Text);
    }

    [AvaloniaFact]
    public void AResizeOnTheFirstPromptDoesNotCostTheMark()
    {
        // The whole of the "first prompt of a session is dead" report. A width change reflows
        // the buffer, which rebuilds the absolute-row coordinate space and bumps
        // ScrollbackPages.Generation - and a resize does NOT make the shell re-emit OSC 133;B
        // (PSReadLine 2.3 repaints the input line without re-running the prompt function). So
        // the pane went markless for the rest of that command line: no passive bubble, no
        // grid-truth query, no structured capture. Since sizing the window is the first thing a
        // user does with a new one, it looked like the first prompt specifically.
        //
        // The mark now lives on the buffer and the reflow re-anchors it; nothing about the
        // generation check was relaxed. See NovaTerminal.VT.Tests.ShellMarkReflowTests.
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("\x1b]133;A\x07user@host:~$ " + PromptEnd + "ec");
        Assert.True(pane.TryGetGridCommandLine(out _));

        pane.Buffer!.Resize(60, 24);

        Assert.True(pane.TryGetGridCommandLine(out GridCommandLine line));
        Assert.Equal("ec", line.Text);
        Assert.Equal(2, line.CursorOffset);
    }

    [AvaloniaFact]
    public void AResizeAfterCommandCompletionStillLeavesNoMark()
    {
        // The re-anchoring must not resurrect a mark OSC 133;D dropped: between one command's
        // end and the next prompt there is no input line, and "no mark" is the honest answer.
        using var pane = new TerminalPane();
        pane.CreateAndWireParser();

        pane.Parser!.Process("\x1b]133;A\x07$ " + PromptEnd + "git status");
        pane.Parser!.Process(CommandExecuted);
        pane.Parser!.Process("\r\n On branch main\r\n");
        pane.Parser!.Process(CommandFinished);

        pane.Buffer!.Resize(60, 24);

        Assert.False(pane.TryGetGridCommandLine(out _));
    }
}
