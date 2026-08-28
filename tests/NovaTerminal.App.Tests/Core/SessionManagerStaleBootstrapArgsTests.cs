using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Sessions saved before this fix carry the shell-integration bootstrap in their persisted
/// arguments, because the pane stored the command line it was *launched* with rather than the one
/// the user configured. Restoring those arguments verbatim makes the integration provider see its
/// own <c>-File</c>, take the "user supplied a script" bail-out, and launch the stale bootstrap
/// path forever — self-perpetuating, because every launch re-saves it.
///
/// Reported after a real 0.5.0 → 0.6.0 update: the first run showed the old execution-policy error
/// even though 0.6.0 no longer launches the bootstrap that way.
/// </summary>
public sealed class SessionManagerStaleBootstrapArgsTests
{
    private static TabSession LeafWithArguments(string arguments) => new()
    {
        Title = "Local",
        Root = new PaneNode
        {
            Type = NodeType.Leaf,
            Command = ShellHelper.GetDefaultShell(),
            Arguments = arguments,
            PaneId = Guid.NewGuid().ToString()
        }
    };

    [AvaloniaFact]
    public void CreateRestoredTabContent_DropsAStaleBootstrapFromPersistedArguments()
    {
        var settings = new TerminalSettings();
        var tabSession = LeafWithArguments(
            @"-NoLogo -NoExit -File C:\Users\x\AppData\Local\NovaTerminal\command-assist\command-assist-bootstrap.ps1");

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(tabSession, settings));

        Assert.DoesNotContain("command-assist-bootstrap.ps1", pane.ShellArgs, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void CreateRestoredTabContent_KeepsArgumentsTheUserActuallyChose()
    {
        // The sanitizer must not become a general-purpose argument filter: a user who
        // configured their own script still gets it.
        var settings = new TerminalSettings();
        var tabSession = LeafWithArguments(@"-NoLogo -File C:\work\my-profile.ps1");

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(tabSession, settings));

        Assert.Contains(@"C:\work\my-profile.ps1", pane.ShellArgs, StringComparison.OrdinalIgnoreCase);
    }
}
