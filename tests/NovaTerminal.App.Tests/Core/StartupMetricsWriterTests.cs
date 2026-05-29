using NovaTerminal.Shell;
using System.Text.Json;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

public sealed class StartupMetricsWriterTests
{
    [Fact]
    public void CreateFromEnvironment_DisabledFlag_ReturnsNull()
    {
        string? previousEnabled = Environment.GetEnvironmentVariable("NOVATERM_STARTUP_METRICS");
        string? previousOut = Environment.GetEnvironmentVariable("NOVATERM_STARTUP_METRICS_OUT");

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_STARTUP_METRICS", null);
            Environment.SetEnvironmentVariable("NOVATERM_STARTUP_METRICS_OUT", null);

            using StartupMetricsWriter? writer = StartupMetricsWriter.CreateFromEnvironment();
            Assert.Null(writer);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_STARTUP_METRICS", previousEnabled);
            Environment.SetEnvironmentVariable("NOVATERM_STARTUP_METRICS_OUT", previousOut);
        }
    }

    [Fact]
    public void TryWriteSnapshot_WritesOneStructuredRecordPerLaunch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-startup-tests", Guid.NewGuid().ToString("N"));
        string outPath = Path.Combine(tempDir, "startup_metrics.jsonl");
        Directory.CreateDirectory(tempDir);

        try
        {
            long now = 500;
            var tracker = new StartupPerformanceTracker(() => now, startTimestamp: 500, timestampFrequency: 1000);
            tracker.TryMark(StartupPhase.MainWindowConstructed);
            now = 545;
            tracker.TryMark(StartupPhase.WindowOpened);
            tracker.TryMarkCheckpoint("MainWindow.AfterSettingsLoad");
            now = 620;
            tracker.TryMark(StartupPhase.FirstTerminalReady);

            using (var writer = StartupMetricsWriter.Create(outPath))
            {
                Assert.NotNull(writer);
                Assert.True(writer!.TryWriteSnapshot(tracker.CreateSnapshot()));
            }

            string[] lines = File.ReadAllLines(outPath);
            Assert.Single(lines);

            using JsonDocument doc = JsonDocument.Parse(lines[0]);
            Assert.Equal(45, doc.RootElement.GetProperty("WindowOpenedMs").GetInt64());
            Assert.Equal(120, doc.RootElement.GetProperty("FirstTerminalReadyMs").GetInt64());
            Assert.True(doc.RootElement.TryGetProperty("LaunchId", out _));
            Assert.Equal(45, doc.RootElement.GetProperty("Checkpoints").GetProperty("MainWindow.AfterSettingsLoad").GetInt64());
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
    public void TryWriteSnapshot_InvalidOutputPath_FailsSafely()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-startup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using StartupMetricsWriter? writer = StartupMetricsWriter.Create(tempDir);
            Assert.Null(writer);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
