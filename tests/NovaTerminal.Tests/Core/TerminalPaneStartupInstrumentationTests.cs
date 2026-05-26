using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using System.Threading;

namespace NovaTerminal.Tests.Core;

public sealed class TerminalPaneStartupInstrumentationTests
{
    [AvaloniaFact]
    public void Constructor_RecordsStartupConstructorCheckpoints()
    {
        long now = 0;
        var tracker = new StartupPerformanceTracker(
            () => Interlocked.Add(ref now, 5),
            startTimestamp: 0,
            timestampFrequency: 1000);

        StartupPerformanceTracker.SetCurrentForTests(tracker);
        try
        {
            var pane = new TerminalPane("pwsh.exe");
            StartupMetricsSnapshot snapshot = tracker.CreateSnapshot();

            Assert.NotNull(pane);
            Assert.NotNull(snapshot.Checkpoints);
            Assert.Contains("TerminalPane.Ctor.AfterInitializeComponent", snapshot.Checkpoints!.Keys);
            Assert.Contains("TerminalPane.Ctor.AfterSetupCommon", snapshot.Checkpoints.Keys);
        }
        finally
        {
            StartupPerformanceTracker.SetCurrentForTests(null);
        }
    }
}
