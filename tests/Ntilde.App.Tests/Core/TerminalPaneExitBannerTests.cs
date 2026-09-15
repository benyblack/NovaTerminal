using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using NovaTerminal.Tests.Infra;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #311: a local pane whose shell died must say so, and must say how to get it back — Enter
/// already restarts it (<c>TerminalPane.ShouldReconnectOnEnter</c> is not SSH-gated), which is
/// exactly the part users could not discover.
/// </summary>
public sealed class TerminalPaneExitBannerTests
{
    [AvaloniaFact]
    public void LocalExitBanner_NonZeroCode_NamesTheCodeAndTheRestartKey()
    {
        using var pane = new TerminalPane(LocalProfile());

        pane.WriteLocalExitBanner(1);

        string visibleText = TerminalBufferText.Visible(pane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Exit code: 1]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Press Enter to restart]", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void LocalExitBanner_CleanExit_OmitsTheExitCodeLine()
    {
        using var pane = new TerminalPane(LocalProfile());

        pane.WriteLocalExitBanner(0);

        string visibleText = TerminalBufferText.Visible(pane.Buffer!);
        Assert.Contains("[Shell exited]", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit code", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Press Enter to restart]", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SshDisconnectBanner_IsUnchanged()
    {
        // #311 must not disturb the banner SSH users already know.
        var profile = new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshHost = "server.example",
            SshUser = "nova"
        };
        using var pane = new TerminalPane(profile);

        pane.HandleSessionExitForTesting(17);

        string visibleText = TerminalBufferText.Visible(pane.Buffer!);
        Assert.Contains("[SSH session disconnected]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Exit code: 17]", visibleText, StringComparison.Ordinal);
        Assert.Contains("[Press Enter to reconnect]", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell exited", visibleText, StringComparison.Ordinal);
    }

    private static TerminalProfile LocalProfile() => new()
    {
        Name = "PowerShell",
        Type = ConnectionType.Local,
        Command = "pwsh.exe"
    };
}
