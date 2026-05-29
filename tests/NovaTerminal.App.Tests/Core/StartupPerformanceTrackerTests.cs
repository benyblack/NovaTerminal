using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using System.Text.Json;

namespace NovaTerminal.Tests.Core;

public sealed class StartupPerformanceTrackerTests
{
    [Fact]
    public void MarkPhase_RecordsElapsedTimeAndPublishesStartupMetrics()
    {
        RendererStatistics.Reset();

        long now = 100;
        var tracker = new StartupPerformanceTracker(() => now, startTimestamp: 100, timestampFrequency: 1000);

        now = 125;
        Assert.True(tracker.TryMark(StartupPhase.MainWindowConstructed));

        now = 140;
        Assert.True(tracker.TryMark(StartupPhase.WindowOpened));

        now = 180;
        Assert.True(tracker.TryMark(StartupPhase.FirstTerminalReady));

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.MainWindowConstructed, out long constructedMs));
        Assert.Equal(25, constructedMs);
        Assert.Equal(40, RendererStatistics.StartupWindowShownTimeMs);
        Assert.Equal(1, RendererStatistics.StartupWindowShownSamples);
        Assert.Equal(80, RendererStatistics.StartupFirstTerminalReadyTimeMs);
        Assert.Equal(1, RendererStatistics.StartupFirstTerminalReadySamples);
    }

    [Fact]
    public void MarkPhase_IgnoresDuplicateWrites()
    {
        RendererStatistics.Reset();

        long now = 200;
        var tracker = new StartupPerformanceTracker(() => now, startTimestamp: 200, timestampFrequency: 1000);

        now = 260;
        Assert.True(tracker.TryMark(StartupPhase.BackgroundRestoreComplete));

        now = 320;
        Assert.False(tracker.TryMark(StartupPhase.BackgroundRestoreComplete));

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out long elapsedMs));
        Assert.Equal(60, elapsedMs);
        Assert.Equal(60, RendererStatistics.StartupBackgroundRestoreTimeMs);
        Assert.Equal(1, RendererStatistics.StartupBackgroundRestoreSamples);
    }

    [Fact]
    public void MarkPhase_WritesStartupSnapshotOnceAfterReadyPhasesComplete()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-startup-tests", Guid.NewGuid().ToString("N"));
        string outPath = Path.Combine(tempDir, "startup_metrics.jsonl");
        Directory.CreateDirectory(tempDir);

        try
        {
            long now = 1000;
            using (var writer = StartupMetricsWriter.Create(outPath))
            {
                Assert.NotNull(writer);

                var tracker = new StartupPerformanceTracker(
                    () => now,
                    startTimestamp: 1000,
                    timestampFrequency: 1000,
                    metricsWriter: writer);

                now = 1020;
                Assert.True(tracker.TryMark(StartupPhase.WindowOpened));
                now = 1080;
                Assert.True(tracker.TryMark(StartupPhase.BackgroundRestoreComplete));

                now = 1150;
                Assert.True(tracker.TryMark(StartupPhase.FirstTerminalReady));
                Assert.True(tracker.TryMark(StartupPhase.SessionRestoreComplete));
                Assert.False(tracker.TryMark(StartupPhase.FirstTerminalReady));
            }

            string[] lines = File.ReadAllLines(outPath);
            Assert.Single(lines);

            using JsonDocument doc = JsonDocument.Parse(lines[0]);
            Assert.Equal(20, doc.RootElement.GetProperty("WindowOpenedMs").GetInt64());
            Assert.Equal(150, doc.RootElement.GetProperty("FirstTerminalReadyMs").GetInt64());
            Assert.Equal(80, doc.RootElement.GetProperty("BackgroundRestoreCompleteMs").GetInt64());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void MarkCheckpoint_RecordsNamedElapsedTimeInSnapshot()
    {
        long now = 100;
        var tracker = new StartupPerformanceTracker(() => now, startTimestamp: 100, timestampFrequency: 1000);

        now = 135;
        Assert.True(tracker.TryMarkCheckpoint("MainWindow.AfterSettingsLoad"));

        now = 145;
        Assert.False(tracker.TryMarkCheckpoint("MainWindow.AfterSettingsLoad"));

        StartupMetricsSnapshot snapshot = tracker.CreateSnapshot();
        Assert.NotNull(snapshot.Checkpoints);
        Assert.Equal(35, Assert.Contains("MainWindow.AfterSettingsLoad", snapshot.Checkpoints!));
    }
}
