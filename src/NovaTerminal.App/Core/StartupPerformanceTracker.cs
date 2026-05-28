using System;
using System.Collections.Generic;
using System.Diagnostics;
using NovaTerminal.Rendering;

namespace NovaTerminal.Core;

public enum StartupPhase
{
    MainWindowConstructed,
    WindowOpened,
    FirstTerminalReady,
    SessionRestoreComplete,
    DeferredWorkComplete,
    BackgroundRestoreComplete
}

public sealed class StartupPerformanceTracker
{
    private readonly Func<long> _timestampProvider;
    private readonly long _startTimestamp;
    private readonly double _ticksToMilliseconds;
    private readonly StartupMetricsWriter? _metricsWriter;
    private readonly Dictionary<StartupPhase, long> _elapsedMillisecondsByPhase = new();
    private readonly Dictionary<string, long> _namedCheckpointMilliseconds = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly string _launchId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private bool _snapshotWritten;

    public static StartupPerformanceTracker? Current { get; private set; }

    public StartupPerformanceTracker()
        : this(Stopwatch.GetTimestamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency, StartupMetricsWriter.CreateFromEnvironment())
    {
    }

    internal StartupPerformanceTracker(Func<long> timestampProvider, long startTimestamp, long timestampFrequency, StartupMetricsWriter? metricsWriter = null)
    {
        ArgumentNullException.ThrowIfNull(timestampProvider);
        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }

        _timestampProvider = timestampProvider;
        _startTimestamp = startTimestamp;
        _ticksToMilliseconds = 1000d / timestampFrequency;
        _metricsWriter = metricsWriter;
    }

    public static StartupPerformanceTracker StartNewCurrent()
    {
        return Current = new StartupPerformanceTracker();
    }

    internal static void SetCurrentForTests(StartupPerformanceTracker? tracker)
    {
        Current = tracker;
    }

    public bool TryMark(StartupPhase phase)
    {
        long elapsedMilliseconds = GetElapsedMilliseconds(_timestampProvider());

        lock (_sync)
        {
            if (_elapsedMillisecondsByPhase.ContainsKey(phase))
            {
                return false;
            }

            _elapsedMillisecondsByPhase[phase] = elapsedMilliseconds;
        }

        Publish(phase, elapsedMilliseconds);
        TryWriteSnapshotIfReady();
        return true;
    }

    public bool TryMarkCheckpoint(string checkpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointName);

        long elapsedMilliseconds = GetElapsedMilliseconds(_timestampProvider());

        lock (_sync)
        {
            if (_namedCheckpointMilliseconds.ContainsKey(checkpointName))
            {
                return false;
            }

            _namedCheckpointMilliseconds[checkpointName] = elapsedMilliseconds;
            return true;
        }
    }

    public bool TryGetElapsedMilliseconds(StartupPhase phase, out long elapsedMilliseconds)
    {
        lock (_sync)
        {
            return _elapsedMillisecondsByPhase.TryGetValue(phase, out elapsedMilliseconds);
        }
    }

    public StartupMetricsSnapshot CreateSnapshot()
    {
        lock (_sync)
        {
            _elapsedMillisecondsByPhase.TryGetValue(StartupPhase.MainWindowConstructed, out long mainWindowConstructedMs);
            _elapsedMillisecondsByPhase.TryGetValue(StartupPhase.WindowOpened, out long windowOpenedMs);
            _elapsedMillisecondsByPhase.TryGetValue(StartupPhase.FirstTerminalReady, out long firstTerminalReadyMs);
            _elapsedMillisecondsByPhase.TryGetValue(StartupPhase.SessionRestoreComplete, out long sessionRestoreCompleteMs);
            _elapsedMillisecondsByPhase.TryGetValue(StartupPhase.DeferredWorkComplete, out long deferredWorkCompleteMs);
            _elapsedMillisecondsByPhase.TryGetValue(StartupPhase.BackgroundRestoreComplete, out long backgroundRestoreCompleteMs);

            return new StartupMetricsSnapshot(
                _launchId,
                _startedAtUtc,
                mainWindowConstructedMs == 0 && !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.MainWindowConstructed) ? null : mainWindowConstructedMs,
                windowOpenedMs == 0 && !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.WindowOpened) ? null : windowOpenedMs,
                firstTerminalReadyMs == 0 && !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.FirstTerminalReady) ? null : firstTerminalReadyMs,
                sessionRestoreCompleteMs == 0 && !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.SessionRestoreComplete) ? null : sessionRestoreCompleteMs,
                deferredWorkCompleteMs == 0 && !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.DeferredWorkComplete) ? null : deferredWorkCompleteMs,
                backgroundRestoreCompleteMs == 0 && !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.BackgroundRestoreComplete) ? null : backgroundRestoreCompleteMs,
                _namedCheckpointMilliseconds.Count == 0 ? null : new Dictionary<string, long>(_namedCheckpointMilliseconds, StringComparer.Ordinal));
        }
    }

    private long GetElapsedMilliseconds(long timestamp)
    {
        long elapsedTicks = Math.Max(0, timestamp - _startTimestamp);
        return (long)Math.Round(elapsedTicks * _ticksToMilliseconds, MidpointRounding.AwayFromZero);
    }

    private static void Publish(StartupPhase phase, long elapsedMilliseconds)
    {
        switch (phase)
        {
            case StartupPhase.WindowOpened:
                RendererStatistics.RecordStartupWindowShown(elapsedMilliseconds);
                break;
            case StartupPhase.FirstTerminalReady:
                RendererStatistics.RecordStartupFirstTerminalReady(elapsedMilliseconds);
                break;
            case StartupPhase.SessionRestoreComplete:
                RendererStatistics.RecordStartupSessionRestoreComplete(elapsedMilliseconds);
                break;
            case StartupPhase.DeferredWorkComplete:
                RendererStatistics.RecordStartupDeferredWork(elapsedMilliseconds);
                break;
            case StartupPhase.BackgroundRestoreComplete:
                RendererStatistics.RecordStartupBackgroundRestore(elapsedMilliseconds);
                break;
        }
    }

    private void TryWriteSnapshotIfReady()
    {
        if (_metricsWriter == null)
        {
            return;
        }

        StartupMetricsSnapshot? snapshot = null;

        lock (_sync)
        {
            if (_snapshotWritten)
            {
                return;
            }

            if (!_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.FirstTerminalReady) ||
                !_elapsedMillisecondsByPhase.ContainsKey(StartupPhase.BackgroundRestoreComplete))
            {
                return;
            }

            _snapshotWritten = true;
            snapshot = CreateSnapshot();
        }

        if (!_metricsWriter.TryWriteSnapshot(snapshot))
        {
            lock (_sync)
            {
                _snapshotWritten = false;
            }
        }
    }
}

public sealed record StartupMetricsSnapshot(
    string LaunchId,
    DateTimeOffset StartedAtUtc,
    long? MainWindowConstructedMs,
    long? WindowOpenedMs,
    long? FirstTerminalReadyMs,
    long? SessionRestoreCompleteMs,
    long? DeferredWorkCompleteMs,
    long? BackgroundRestoreCompleteMs,
    IReadOnlyDictionary<string, long>? Checkpoints);
