using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NovaTerminal.Shell;

public sealed record StartupPhaseStatistics(
    int Count,
    double AverageMs,
    double MedianMs,
    long MinMs,
    long MaxMs);

public sealed record StartupMetricsSummary(
    int LaunchCount,
    IReadOnlyDictionary<StartupPhase, StartupPhaseStatistics> Phases);

public sealed record StartupPhaseComparison(
    double BaselineAverageMs,
    double CandidateAverageMs,
    double DeltaMs,
    double ImprovementPercent);

public sealed record StartupMetricsComparison(
    int BaselineLaunchCount,
    int CandidateLaunchCount,
    IReadOnlyDictionary<StartupPhase, StartupPhaseComparison> Phases);

public static class StartupMetricsAnalysis
{
    private static readonly StartupPhase[] OrderedPhases =
    {
        StartupPhase.MainWindowConstructed,
        StartupPhase.WindowOpened,
        StartupPhase.FirstTerminalReady,
        StartupPhase.SessionRestoreComplete,
        StartupPhase.DeferredWorkComplete,
        StartupPhase.BackgroundRestoreComplete
    };

    public static IReadOnlyList<StartupMetricsSnapshot> LoadSnapshots(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        var snapshots = new List<StartupMetricsSnapshot>();

        if (Directory.Exists(fullPath))
        {
            foreach (string file in Directory.EnumerateFiles(fullPath, "*.jsonl", SearchOption.AllDirectories).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                snapshots.AddRange(LoadSnapshotsFromFile(file));
            }

            return snapshots;
        }

        if (File.Exists(fullPath))
        {
            return LoadSnapshotsFromFile(fullPath);
        }

        throw new FileNotFoundException("Startup metrics input path was not found.", fullPath);
    }

    public static StartupMetricsSummary Summarize(IEnumerable<StartupMetricsSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        StartupMetricsSnapshot[] snapshotArray = snapshots.ToArray();
        var phases = new Dictionary<StartupPhase, StartupPhaseStatistics>();

        foreach (StartupPhase phase in OrderedPhases)
        {
            long[] values = snapshotArray
                .Select(snapshot => GetPhaseValue(snapshot, phase))
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .OrderBy(static value => value)
                .ToArray();

            if (values.Length == 0)
            {
                continue;
            }

            phases[phase] = new StartupPhaseStatistics(
                Count: values.Length,
                AverageMs: values.Average(),
                MedianMs: GetMedian(values),
                MinMs: values[0],
                MaxMs: values[^1]);
        }

        return new StartupMetricsSummary(snapshotArray.Length, phases);
    }

    public static StartupMetricsComparison Compare(IEnumerable<StartupMetricsSnapshot> baseline, IEnumerable<StartupMetricsSnapshot> candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        StartupMetricsSummary baselineSummary = Summarize(baseline);
        StartupMetricsSummary candidateSummary = Summarize(candidate);
        var phases = new Dictionary<StartupPhase, StartupPhaseComparison>();

        foreach (StartupPhase phase in OrderedPhases)
        {
            if (!baselineSummary.Phases.TryGetValue(phase, out StartupPhaseStatistics? baselinePhase) ||
                !candidateSummary.Phases.TryGetValue(phase, out StartupPhaseStatistics? candidatePhase))
            {
                continue;
            }

            double deltaMs = candidatePhase.AverageMs - baselinePhase.AverageMs;
            double improvementPercent = baselinePhase.AverageMs <= 0
                ? 0
                : ((baselinePhase.AverageMs - candidatePhase.AverageMs) / baselinePhase.AverageMs) * 100d;

            phases[phase] = new StartupPhaseComparison(
                BaselineAverageMs: baselinePhase.AverageMs,
                CandidateAverageMs: candidatePhase.AverageMs,
                DeltaMs: deltaMs,
                ImprovementPercent: improvementPercent);
        }

        return new StartupMetricsComparison(baselineSummary.LaunchCount, candidateSummary.LaunchCount, phases);
    }

    public static string BuildMarkdownReport(StartupMetricsComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var lines = new List<string>
        {
            "# Startup Performance Report",
            string.Empty,
            $"Baseline launches: {comparison.BaselineLaunchCount}",
            $"Candidate launches: {comparison.CandidateLaunchCount}",
            string.Empty,
            "| Phase | Baseline Avg (ms) | Candidate Avg (ms) | Delta (ms) | Improvement |",
            "| --- | ---: | ---: | ---: | ---: |"
        };

        foreach ((StartupPhase phase, StartupPhaseComparison values) in comparison.Phases.OrderBy(static entry => Array.IndexOf(OrderedPhases, entry.Key)))
        {
            lines.Add(
                $"| {GetPhaseLabel(phase)} | {values.BaselineAverageMs:F2} | {values.CandidateAverageMs:F2} | {values.DeltaMs:F2} | {values.ImprovementPercent:F2}% |");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<StartupMetricsSnapshot> LoadSnapshotsFromFile(string filePath)
    {
        var snapshots = new List<StartupMetricsSnapshot>();

        foreach (string rawLine in File.ReadLines(filePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            StartupMetricsSnapshot? snapshot = JsonSerializer.Deserialize(line, StartupMetricsSerializationContext.Default.StartupMetricsSnapshot);
            if (snapshot != null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private static long? GetPhaseValue(StartupMetricsSnapshot snapshot, StartupPhase phase)
    {
        return phase switch
        {
            StartupPhase.MainWindowConstructed => snapshot.MainWindowConstructedMs,
            StartupPhase.WindowOpened => snapshot.WindowOpenedMs,
            StartupPhase.FirstTerminalReady => snapshot.FirstTerminalReadyMs,
            StartupPhase.SessionRestoreComplete => snapshot.SessionRestoreCompleteMs,
            StartupPhase.DeferredWorkComplete => snapshot.DeferredWorkCompleteMs,
            StartupPhase.BackgroundRestoreComplete => snapshot.BackgroundRestoreCompleteMs,
            _ => null
        };
    }

    private static double GetMedian(long[] values)
    {
        int midpoint = values.Length / 2;
        if ((values.Length & 1) == 1)
        {
            return values[midpoint];
        }

        return (values[midpoint - 1] + values[midpoint]) / 2d;
    }

    private static string GetPhaseLabel(StartupPhase phase)
    {
        return phase switch
        {
            StartupPhase.MainWindowConstructed => "Main Window Constructed",
            StartupPhase.WindowOpened => "Window Opened",
            StartupPhase.FirstTerminalReady => "First Terminal Ready",
            StartupPhase.SessionRestoreComplete => "Session Restore Complete",
            StartupPhase.DeferredWorkComplete => "Deferred Work Complete",
            StartupPhase.BackgroundRestoreComplete => "Background Restore Complete",
            _ => phase.ToString()
        };
    }
}
