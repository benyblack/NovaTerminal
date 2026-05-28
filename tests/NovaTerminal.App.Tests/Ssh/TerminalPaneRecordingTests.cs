using System.Reflection;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests.Ssh;

public sealed class TerminalPaneRecordingTests
{
    [AvaloniaFact]
    public void ToggleRecording_StartsAndStopsWithoutWritingTerminalText_AndPublishesMetadata()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "PowerShell",
            Type = ConnectionType.Local,
            Command = "pwsh.exe"
        });

        try
        {
            var session = new StubTerminalSession();
            SetSessionForTest(pane, session);

            string before = GetVisiblePlainText(pane.Buffer!);
            var notifications = new List<RecordingNotificationEventArgs>();
            pane.RecordingNotification += notifications.Add;

            pane.ToggleRecording();
            pane.ToggleRecording();

            string after = GetVisiblePlainText(pane.Buffer!);

            Assert.Equal(before, after);
            Assert.Equal(2, notifications.Count);

            var started = notifications[0];
            Assert.Equal(RecordingNotificationKind.Started, started.Kind);
            Assert.True(started.IsRecording);
            Assert.Equal(session.StartedPath, started.FilePath);
            Assert.StartsWith(AppPaths.RecordingsDirectory, started.FilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"^nova_rec_\d{8}_\d{6}_[0-9a-f]{6}\.rec$", Path.GetFileName(started.FilePath));

            var stopped = notifications[1];
            Assert.Equal(RecordingNotificationKind.Stopped, stopped.Kind);
            Assert.False(stopped.IsRecording);
            Assert.Equal(session.StartedPath, stopped.FilePath);
            Assert.Equal(AppPaths.RecordingsDirectory, stopped.RecordingsDirectory);
        }
        finally
        {
            pane.Dispose();
        }
    }

    [Fact]
    public void BuildRecordingFileName_AppendsStableUniqueSuffix()
    {
        string first = TerminalPane.BuildRecordingFileName(
            new DateTime(2026, 5, 22, 10, 30, 45),
            "a1b2c3d4");
        string second = TerminalPane.BuildRecordingFileName(
            new DateTime(2026, 5, 22, 10, 30, 45),
            "f0e1d2c3");

        Assert.Equal("nova_rec_20260522_103045_a1b2c3.rec", first);
        Assert.Equal("nova_rec_20260522_103045_f0e1d2.rec", second);
        Assert.NotEqual(first, second);
    }

    private static void SetSessionForTest(TerminalPane pane, ITerminalSession session)
    {
        var property = typeof(TerminalPane).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        property!.SetValue(pane, session);
    }

    private static string GetVisiblePlainText(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance);
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
        public Guid Id => Guid.NewGuid();
        public string ShellCommand => "stub";
        public string? ShellArguments => null;
        public bool IsProcessRunning => true;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public bool IsRecording { get; private set; }
        public string? StartedPath { get; private set; }

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
            StartedPath = filePath;
            IsRecording = true;
        }

        public void StopRecording()
        {
            IsRecording = false;
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
