using Avalonia.Headless.XUnit;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// The V1 desync matrix, driven through the real stack: escape sequences into
/// <c>TerminalPane.Parser</c>, out through the grid reader, the App-boundary snapshot mapping and
/// the controller's lifecycle gate.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario here is a case where the shell rewrites the command line and emits nothing that a
/// keystroke mirror could observe. V1 mirrored TextInput, Backspace, Enter and Paste; <c>Ctrl+U</c>,
/// history recall and shell-side Tab completion produce none of those, so its query drifted and
/// stayed drifted for the rest of the command. What is asserted is that the pane's view of the
/// command line is whatever is painted, with no reference to how it got there.
/// </para>
/// <para>
/// Sibling coverage: <c>PaneGridCommandLineTests</c> pins the mark plumbing,
/// <c>NovaTerminal.VT.Tests.GridQueryReaderTests</c> pins extraction, and
/// <c>CommandAssistGridTruthTests</c> pins the same scenarios against the controller seam without a
/// terminal. This file is the one that proves the pieces are actually connected.
/// </para>
/// </remarks>
public class PaneGridTruthDesyncTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";
    private const string CommandExecuted = "\x1b]133;C\x07";
    private const string CommandFinished = "\x1b]133;D;0\x07";
    private const string EraseLine = "\r\x1b[K";

    /// <summary>
    /// <c>Ctrl+U</c>: the shell erases the line and reprints the prompt. Nothing reaches the App as
    /// a key event Command Assist watches, so V1's query kept the erased text forever.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterCtrlU_TheQueryIsTheEmptiedLine()
    {
        using var pane = CreatePane();
        await AtAPromptAsync(pane, "git st");

        Assert.Equal("git st", ReadQuery(pane)?.Text);

        // What Ctrl+U actually looks like on the wire: carriage return, erase to end of line,
        // prompt reprinted (which re-emits B), nothing after it.
        pane.Parser!.Process(EraseLine + PromptStart + "$ " + PromptEnd);

        Assert.Equal(string.Empty, ReadQuery(pane)?.Text);
    }

    /// <summary>
    /// Up-arrow history recall. The line becomes a command the user typed no character of, and it
    /// is longer than anything they typed - the case where V1 would compute an insertion delta
    /// against a prefix that was not on the line.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterHistoryRecall_TheQueryIsTheRecalledCommand()
    {
        using var pane = CreatePane();
        await AtAPromptAsync(pane, "git");

        pane.Parser!.Process(EraseLine + PromptStart + "$ " + PromptEnd + "git status --short");

        AssistQuerySnapshot? line = ReadQuery(pane);
        Assert.Equal("git status --short", line?.Text);

        // And the planner now computes against that, not against "git".
        Assert.True(CommandAssistInsertionPlanner.TryCreateInsertion(
            line,
            "git status --short --branch",
            out string? textToSend));
        Assert.Equal(" --branch", textToSend);
    }

    /// <summary>
    /// Shell-side Tab completion. Command Assist deliberately does not own Tab, so the completed
    /// text arrives as painted cells with no key event at all.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterShellTabCompletion_TheQueryIsTheCompletedWord()
    {
        using var pane = CreatePane();
        await AtAPromptAsync(pane, "cd Nova");

        // The shell finishes the word in place.
        pane.Parser!.Process("Terminal.CommandAssist/");

        Assert.Equal("cd NovaTerminal.CommandAssist/", ReadQuery(pane)?.Text);
    }

    /// <summary>
    /// A left-arrow moves the cursor without changing the text. The text is still the query - it is
    /// what the user will run - but it is no longer a prefix an append can extend, and the snapshot
    /// says so.
    /// </summary>
    /// <remarks>
    /// Replace refuses on the same read, and for a sharper reason: its deletes run leftwards from the
    /// cursor, so a replace here would eat <c>git sta</c>, leave <c>tus</c> behind, and insert the
    /// command in front of the survivor.
    /// </remarks>
    [AvaloniaFact]
    public async Task AfterAnArrowKey_TheTextIsUnchangedButItIsNoLongerAnAppendableParent()
    {
        using var pane = CreatePane();
        await AtAPromptAsync(pane, "git status");

        pane.Parser!.Process("\x1b[D\x1b[D\x1b[D");

        AssistQuerySnapshot? line = ReadQuery(pane);
        Assert.Equal("git status", line?.Text);
        Assert.Equal(7, line?.CursorOffset);
        Assert.False(line?.IsUsableAsTypedPrefix);
        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(line, "git status --short", out _));
        Assert.False(CommandAssistInsertionPlanner.TryCreatePlan(
            line,
            "git status --short",
            CommandAssistInsertionStyle.ReplaceTypedPrefix,
            out _));
    }

    /// <summary>
    /// The lifecycle gate at the pane level. After <c>D</c> the pane drops the mark, so even the
    /// raw seam goes dark rather than serving the command's output as a command line.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterTheCommandFinishes_ThereIsNoQuery()
    {
        using var pane = CreatePane();
        await AtAPromptAsync(pane, "git status");

        pane.Parser!.Process(CommandExecuted + "\r\nOn branch main\r\n" + CommandFinished);

        Assert.Null(ReadQuery(pane));
    }

    /// <summary>
    /// The provider the controller is constructed with - reader, mark lifecycle and App-boundary
    /// mapping, exactly as production wires them. The controller's own lifecycle gate sits above
    /// this and is covered by <c>CommandAssistGridTruthTests</c> and the pane-level Help tests in
    /// <c>TerminalPaneCommandAssistShortcutTests</c>; what this file is for is the layer below it.
    /// </summary>
    private static AssistQuerySnapshot? ReadQuery(TerminalPane pane) => pane.TryReadAssistQuerySnapshot();

    /// <summary>Paints an integrated prompt and types <paramref name="commandLine"/> at it.</summary>
    private static async Task AtAPromptAsync(TerminalPane pane, string commandLine)
    {
        pane.Parser!.Process(PromptStart + "$ " + PromptEnd + commandLine);

        // The shell-integration event dispatcher is serialized and asynchronous; the lifecycle gate
        // opens when B reaches the controller through it.
        await Task.Delay(50);
    }

    private static TerminalPane CreatePane()
    {
        var pane = new TerminalPane();
        pane.CommandAssistServices = TestCommandAssistServices.Instance;
        var settings = new TerminalSettings(); // constructed, not Load() - see #232
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;
        pane.ApplySettings(settings);
        pane.ArmShellIntegrationTracker();
        pane.CreateAndWireParser();
        return pane;
    }
}
