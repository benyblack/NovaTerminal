using NovaTerminal.Shell;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests.Ssh;

public sealed class TerminalPaneSshDisconnectTests
{
    [Fact]
    public void ShouldReconnectOnEnter_ReturnsFalseForRunningSession()
    {
        Assert.False(TerminalPane.ShouldReconnectOnEnter(new StubTerminalSession(isProcessRunning: true)));
    }

    [Fact]
    public void ShouldReconnectOnEnter_ReturnsTrueForStoppedSession()
    {
        Assert.True(TerminalPane.ShouldReconnectOnEnter(new StubTerminalSession(isProcessRunning: false)));
    }

    [AvaloniaFact]
    public void HandleSessionExit_ForSshProfile_WritesDisconnectedBanner()
    {
        var profile = new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshHost = "server.example",
            SshUser = "nova"
        };
        using var pane = new TerminalPane(profile);

        pane.HandleSessionExitForTesting(17);

        string visibleText = GetVisiblePlainText(pane.Buffer!);
        Assert.Equal(17, pane.LastExitCode);
        Assert.Contains("SSH session disconnected", visibleText, StringComparison.Ordinal);
        Assert.Contains("Press Enter to reconnect", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void HandleSessionExit_ForLocalProfile_DoesNotWriteSshDisconnectedBanner()
    {
        var profile = new TerminalProfile
        {
            Name = "PowerShell",
            Type = ConnectionType.Local,
            Command = "pwsh.exe"
        };
        using var pane = new TerminalPane(profile);

        pane.HandleSessionExitForTesting(5);

        string visibleText = GetVisiblePlainText(pane.Buffer!);
        Assert.Equal(5, pane.LastExitCode);
        Assert.DoesNotContain("SSH session disconnected", visibleText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void RealEnterKeyPress_AfterDisconnect_BubblesToPaneAndTriggersReconnect()
    {
        // End-to-end through Avalonia's real input pipeline: a dead session is left attached to
        // the focused TerminalView (as happens after an SSH disconnect). Pressing Enter must NOT
        // be swallowed into the dead PTY — it has to bubble TerminalView -> TerminalPane.OnKeyDown,
        // whose reconnect handler runs Reconnect(), which writes a "[Reconnecting...]" marker.
        var profile = new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshHost = "server.example",
            SshUser = "nova"
        };
        var deadSession = new Mock<ITerminalSession>();
        deadSession.SetupGet(x => x.IsProcessRunning).Returns(false);

        using var pane = new TerminalPane(profile);
        var view = pane.FindControl<TerminalView>("TermView");
        Assert.NotNull(view);
        view!.SetSession(deadSession.Object);

        var window = new Window { Content = pane, Width = 600, Height = 400 };
        try
        {
            window.Show();
            view.Focus();
            Assert.True(pane.IsKeyboardFocusWithin);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");

            // Enter was not forwarded to the dead session...
            deadSession.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
            // ...and the pane's reconnect path ran.
            Assert.Contains("Reconnecting", GetVisiblePlainText(pane.Buffer!), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private static string GetVisiblePlainText(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var viewport = (TerminalRow[])field!.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(GetRowText)).TrimEnd();
    }

    private static string GetRowText(TerminalRow row)
    {
        char[] chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }

    private sealed class StubTerminalSession : ITerminalSession
    {
        public StubTerminalSession(bool isProcessRunning)
        {
            IsProcessRunning = isProcessRunning;
        }

        public Guid Id => Guid.NewGuid();
        public string ShellCommand => "stub";
        public string? ShellArguments => null;
        public bool IsProcessRunning { get; }
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public bool IsRecording => false;
        public event Action<string>? OnOutputReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? OnExit
        {
            add { }
            remove { }
        }
        public void SendInput(string input)
        {
        }

        public void Resize(int cols, int rows)
        {
        }

        public void StartRecording(string filePath)
        {
        }

        public void StopRecording()
        {
        }

        public void AttachBuffer(TerminalBuffer buffer)
        {
        }

        public void TakeSnapshot()
        {
        }

        public void Dispose()
        {
        }
    }
}
