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
}
