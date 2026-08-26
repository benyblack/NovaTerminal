using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Shell;

/// <summary>
/// The session file must never carry — or act on — a blank shell command.
/// </summary>
/// <remarks>
/// Field report: every launch opened a pane showing
/// <c>Failed to spawn process:</c> with no command named, and
/// <c>failed to spawn '': CreateProcessW `"...\VMware Workstation\bin\\"` ... Access is denied</c>.
/// The user's <c>last_session.json</c> held a single leaf with <c>"Command": ""</c>:
/// <list type="number">
///   <item>a pane that had not started a session yet reports <c>ShellCommand == string.Empty</c>,
///         and the capture wrote that out verbatim;</item>
///   <item>on restore, <c>node.Command ?? "cmd.exe"</c> did not fire — <c>""</c> is not null — so
///         the pane was built with an empty command;</item>
///   <item>the pane spawned it, failed, and kept <c>ShellCommand == ""</c>, which the next
///         shutdown persisted again.</item>
/// </list>
/// Self-sustaining: once written, the terminal was dead on every subsequent launch and no
/// amount of restarting cleared it. Both halves are covered here — the write must not produce
/// a blank, and the read must not trust one, because session files written by the old code are
/// still on disk.
/// </remarks>
public class SessionEmptyCommandTests
{
    private static TabSession LeafTab(string? command) => new()
    {
        Title = "Restored",
        Root = new PaneNode
        {
            Type = NodeType.Leaf,
            Command = command,
            Arguments = string.Empty,
            PaneId = Guid.NewGuid().ToString()
        }
    };

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Restoring_a_leaf_without_a_usable_command_falls_back_to_the_default_shell(string? command)
    {
        TabItem restored = SessionManager.CreateRestoredTabItem(LeafTab(command), new TerminalSettings())!;

        var pane = (TerminalPane)restored.Content!;
        Assert.Equal(ShellHelper.GetDefaultShell(), pane.ShellCommand);
    }

    [AvaloniaFact]
    public void Restoring_a_leaf_with_a_real_command_keeps_it()
    {
        // The fallback must not swallow a command that was perfectly good.
        //
        // Deliberately not "cmd.exe": the restore fallback now rejects a command that cannot run
        // on *this* platform, not just a blank one, so cmd.exe is a fine example of a good command
        // on Windows and an example of the opposite everywhere else. Environment.ProcessPath is a
        // real executable on every platform, and it is never the default shell, so this still
        // distinguishes "kept" from "fell back".
        string realCommand = Environment.ProcessPath!;
        Assert.NotEqual(ShellHelper.GetDefaultShell(), realCommand);

        TabItem restored = SessionManager.CreateRestoredTabItem(LeafTab(realCommand), new TerminalSettings())!;

        var pane = (TerminalPane)restored.Content!;
        Assert.Equal(realCommand, pane.ShellCommand);
    }

    [AvaloniaFact]
    public void Capturing_a_pane_that_never_started_a_session_records_no_command()
    {
        // A pane is only measured once it is laid out, so a window closed early captures panes
        // in exactly this state. `null` (not "") is what makes the restore fallback fire.
        using var pane = new TerminalPane();
        Assert.Equal(string.Empty, pane.ShellCommand);

        NovaSession captured = CaptureSingle(pane);

        Assert.Null(captured.Tabs[0].Root!.Command);
    }

    [AvaloniaFact]
    public void A_captured_and_restored_unstarted_pane_is_still_spawnable()
    {
        // The full loop the bug lived in: capture an unstarted pane, restore it, and the
        // restored pane must have something it can actually run. Before the fix this round-trip
        // produced ""  -> "" -> a spawn of the first %PATH% directory.
        using var pane = new TerminalPane();
        NovaSession captured = CaptureSingle(pane);

        TabItem restored = SessionManager.CreateRestoredTabItem(captured.Tabs[0], new TerminalSettings())!;

        var restoredPane = (TerminalPane)restored.Content!;
        Assert.False(
            string.IsNullOrWhiteSpace(restoredPane.ShellCommand),
            "a restored pane must carry a command it can spawn");
    }

    private static NovaSession CaptureSingle(TerminalPane pane)
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Terminal", Content = pane });
        var window = new Window { Content = tabs };

        return SessionManager.CaptureSession(window, tabs);
    }
}
