using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class TabClosePolicyTests
{
    [Fact]
    public void NonRunningPane_IsAlwaysAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: false,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellArgs: "",
            paneClosePolicy: "confirm");

        Assert.True(accepted);
    }

    [Fact]
    public void IdleLocalShell_IsAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasUserInteraction: false,
            profileType: ConnectionType.Local,
            shellArgs: "",
            paneClosePolicy: "confirm");

        Assert.True(accepted);
    }

    [Fact]
    public void IdleSshShell_IsNotAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasUserInteraction: false,
            profileType: ConnectionType.SSH,
            shellArgs: "",
            paneClosePolicy: "confirm");

        Assert.False(accepted);
    }

    [Fact]
    public void RunningInteractedPane_WithForcePolicy_IsAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellArgs: "-NoExit",
            paneClosePolicy: "force");

        Assert.True(accepted);
    }

    [Fact]
    public void RunningInteractedPane_WithConfirmPolicy_IsNotAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellArgs: "-NoExit",
            paneClosePolicy: "confirm");

        Assert.False(accepted);
    }
}
