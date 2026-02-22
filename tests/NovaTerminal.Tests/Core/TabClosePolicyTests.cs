using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class TabClosePolicyTests
{
    [Fact]
    public void NonRunningPane_IsAlwaysAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: false,
            hasActiveChildProcesses: false,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellCommand: "",
            shellArgs: "",
            paneClosePolicy: "confirm");

        Assert.True(accepted);
    }

    [Fact]
    public void IdleLocalShell_IsAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasActiveChildProcesses: false,
            hasUserInteraction: false,
            profileType: ConnectionType.Local,
            shellCommand: "",
            shellArgs: "",
            paneClosePolicy: "confirm");

        Assert.True(accepted);
    }

    [Fact]
    public void IdleSshShell_IsNotAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasActiveChildProcesses: false,
            hasUserInteraction: false,
            profileType: ConnectionType.SSH,
            shellCommand: "",
            shellArgs: "",
            paneClosePolicy: "confirm");

        Assert.False(accepted);
    }

    [Fact]
    public void RunningInteractedPane_WithForcePolicy_IsAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasActiveChildProcesses: true,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellCommand: "",
            shellArgs: "-NoExit",
            paneClosePolicy: "force");

        Assert.True(accepted);
    }

    [Fact]
    public void RunningInteractedPane_WithConfirmPolicy_IsNotAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasActiveChildProcesses: true,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellCommand: "",
            shellArgs: "-NoExit",
            paneClosePolicy: "confirm");

        Assert.False(accepted);
    }

    [Fact]
    public void WslIdledPane_IsAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasActiveChildProcesses: false,
            hasUserInteraction: false,
            profileType: ConnectionType.Local,
            shellCommand: "wsl.exe",
            shellArgs: "-d Ubuntu-22.04",
            paneClosePolicy: "confirm");

        Assert.True(accepted);
    }

    [Fact]
    public void WslInteractedPane_IsNotAutoAccepted()
    {
        bool accepted = NovaTerminal.MainWindow.ShouldAutoAcceptRunningPaneClose(
            isProcessRunning: true,
            hasActiveChildProcesses: false,
            hasUserInteraction: true,
            profileType: ConnectionType.Local,
            shellCommand: "wsl.exe",
            shellArgs: "-d Ubuntu-22.04",
            paneClosePolicy: "confirm");

        Assert.False(accepted);
    }
}
