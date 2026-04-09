using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Core;

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
        var pane = new TerminalPane(profile);

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
        var pane = new TerminalPane(profile);

        pane.HandleSessionExitForTesting(5);

        string visibleText = GetVisiblePlainText(pane.Buffer!);
        Assert.Equal(5, pane.LastExitCode);
        Assert.DoesNotContain("SSH session disconnected", visibleText, StringComparison.Ordinal);
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
