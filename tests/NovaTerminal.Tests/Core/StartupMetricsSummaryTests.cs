using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class StartupMetricsSummaryTests
{
    [Fact]
    public void Summarize_ComputesCountAverageAndMedianByPhase()
    {
        var snapshots = new[]
        {
            new StartupMetricsSnapshot("a", DateTimeOffset.UtcNow, 10, 40, 100, 150, 180, 220, null),
            new StartupMetricsSnapshot("b", DateTimeOffset.UtcNow, 12, 50, 90, 140, 170, 210, null),
            new StartupMetricsSnapshot("c", DateTimeOffset.UtcNow, null, 60, 110, null, 190, 230, null)
        };

        StartupMetricsSummary summary = StartupMetricsAnalysis.Summarize(snapshots);

        Assert.Equal(3, summary.LaunchCount);

        StartupPhaseStatistics windowShown = AssertPhase(summary, StartupPhase.WindowOpened);
        Assert.Equal(3, windowShown.Count);
        Assert.Equal(50d, windowShown.AverageMs);
        Assert.Equal(50d, windowShown.MedianMs);

        StartupPhaseStatistics restoreComplete = AssertPhase(summary, StartupPhase.SessionRestoreComplete);
        Assert.Equal(2, restoreComplete.Count);
        Assert.Equal(145d, restoreComplete.AverageMs);
        Assert.Equal(145d, restoreComplete.MedianMs);
    }

    [Fact]
    public void Compare_ComputesDeltaAndImprovementPercentage()
    {
        var baseline = new[]
        {
            new StartupMetricsSnapshot("a", DateTimeOffset.UtcNow, null, 45, 120, 170, 210, 260, null),
            new StartupMetricsSnapshot("b", DateTimeOffset.UtcNow, null, 55, 110, 180, 220, 240, null)
        };

        var candidate = new[]
        {
            new StartupMetricsSnapshot("c", DateTimeOffset.UtcNow, null, 35, 90, 140, 180, 200, null),
            new StartupMetricsSnapshot("d", DateTimeOffset.UtcNow, null, 45, 80, 130, 170, 190, null)
        };

        StartupMetricsComparison comparison = StartupMetricsAnalysis.Compare(baseline, candidate);

        Assert.Equal(2, comparison.BaselineLaunchCount);
        Assert.Equal(2, comparison.CandidateLaunchCount);

        StartupPhaseComparison firstTerminal = AssertPhase(comparison, StartupPhase.FirstTerminalReady);
        Assert.Equal(115d, firstTerminal.BaselineAverageMs);
        Assert.Equal(85d, firstTerminal.CandidateAverageMs);
        Assert.Equal(-30d, firstTerminal.DeltaMs);
        Assert.Equal(26.09d, firstTerminal.ImprovementPercent, 2);

        StartupPhaseComparison backgroundRestore = AssertPhase(comparison, StartupPhase.BackgroundRestoreComplete);
        Assert.Equal(250d, backgroundRestore.BaselineAverageMs);
        Assert.Equal(195d, backgroundRestore.CandidateAverageMs);
        Assert.Equal(-55d, backgroundRestore.DeltaMs);
        Assert.Equal(22d, backgroundRestore.ImprovementPercent, 2);
    }

    private static StartupPhaseStatistics AssertPhase(StartupMetricsSummary summary, StartupPhase phase)
    {
        return Assert.Contains(phase, summary.Phases);
    }

    private static StartupPhaseComparison AssertPhase(StartupMetricsComparison comparison, StartupPhase phase)
    {
        return Assert.Contains(phase, comparison.Phases);
    }
}
