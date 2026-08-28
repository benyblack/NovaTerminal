using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Backup;

/// <summary>
/// Watches the backed-up paths and writes one snapshot after changes go quiet.
///
/// The debounce matters: a settings save touches several files in quick succession, and each
/// one raises multiple <see cref="FileSystemWatcher"/> events. Without coalescing, a single
/// save would produce a burst of snapshots that the hash dedupe would then mostly discard —
/// wasted work on every keystroke in the settings window.
/// </summary>
public sealed class SnapshotScheduler : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A continuously-busy tree (a long-running session appending to files under a watched,
    /// backed-up path) must still get a snapshot eventually rather than never, since each new
    /// change keeps resetting the trailing debounce forever (I2). This caps the total wait from
    /// the first pending change, independent of how many further changes arrive in the meantime.
    /// </summary>
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMinutes(5);

    private readonly BackupService _service;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _maxDelay;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _log;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();

    private Timer? _timer;
    private bool _pending;
    private DateTimeOffset? _pendingSince;
    private bool _disposed;
    private bool _started;

    /// <summary>
    /// Test-only hook invoked from <see cref="FlushAsync"/> immediately before it calls
    /// <see cref="BackupService.Snapshot"/> — i.e. after the gate has been acquired and the
    /// pending flag cleared, but before the potentially slow file I/O. Lets a test deterministically
    /// simulate <see cref="Dispose"/> racing an in-flight flush on FlushAsync's own call stack,
    /// rather than relying on real thread timing to hit a narrow window.
    /// </summary>
    internal Action? BeforeSnapshotForTest;

    /// <summary>
    /// Test-only seam: the delay <see cref="NotifyChanged"/> most recently scheduled the timer
    /// for. Lets a test pin the I2 max-delay cap deterministically (via an injected
    /// <see cref="TimeProvider"/>) without waiting on the real <see cref="Timer"/> to fire.
    /// </summary>
    internal TimeSpan LastScheduledDelayForTest { get; private set; }

    public SnapshotScheduler(
        BackupService service,
        TimeSpan? debounce = null,
        Action<string>? log = null,
        TimeSpan? maxDelay = null,
        TimeProvider? timeProvider = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _debounce = debounce ?? DefaultDebounce;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log ?? (static _ => { });
    }

    /// <summary>
    /// Begins watching. Best-effort: a watcher that cannot be created (missing directory,
    /// inotify limit reached on Linux) is skipped rather than failing app startup. Calling this
    /// more than once is a no-op — otherwise a second call would register duplicate watchers and
    /// every real change would dispatch <see cref="NotifyChanged"/> once per registration.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        foreach (string directory in WatchedDirectories())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;

                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                watcher.Error += (_, e) => _log($"[backup] watcher error: {e.GetException().Message}");

                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _log($"[backup] could not watch {directory}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Marks a change pending and (re)starts the debounce timer. A continuously busy tree keeps
    /// calling this before the debounce ever elapses, so the delay scheduled here is capped at
    /// <c>_maxDelay</c> measured from the FIRST change in the current pending streak (I2) —
    /// otherwise a tree that is never quiet for <c>_debounce</c> would never get a snapshot at
    /// all.
    /// </summary>
    public void NotifyChanged()
    {
        if (_disposed) return;

        lock (_timerLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (!_pending)
            {
                _pending = true;
                _pendingSince = now;
            }

            var elapsed = now - (_pendingSince ?? now);
            var remainingBudget = _maxDelay - elapsed;
            var delay = remainingBudget < _debounce
                ? (remainingBudget < TimeSpan.Zero ? TimeSpan.Zero : remainingBudget)
                : _debounce;

            LastScheduledDelayForTest = delay;

            _timer ??= new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Writes a snapshot now if one is pending. Returns null when nothing was pending or the
    /// snapshot was deduped away. Serialized, so overlapping timer fires cannot double-write.
    /// </summary>
    public async Task<SnapshotInfo?> FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_timerLock)
            {
                if (!_pending) return null;
                _pending = false;
                _pendingSince = null;
            }

            BeforeSnapshotForTest?.Invoke();
            return _service.Snapshot(SnapshotReason.Auto);
        }
        finally
        {
            // A concurrent Dispose() can dispose _gate while this call still holds it (Snapshot()
            // is real file I/O, so the window is not vanishingly small): SemaphoreSlim.Dispose()
            // succeeds immediately even while "held", and Release() on an already-disposed
            // semaphore throws ObjectDisposedException. Thrown from a finally, that exception
            // would replace the SnapshotInfo just computed above with a fault — the caller would
            // see an exception instead of the snapshot that is genuinely sitting on disk. Swallow
            // it: the snapshot already succeeded: there is nothing left to release into.
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Distinct existing directories covering every catalog entry, with any directory dropped
    /// that is already covered by another (shorter) directory in the set — a watcher always runs
    /// with <see cref="FileSystemWatcher.IncludeSubdirectories"/> true, so a nested watcher never
    /// sees anything its ancestor doesn't. In practice this collapses to just RootDirectory
    /// itself, since the Settings catalog entry resolves there.
    /// </summary>
    private IEnumerable<string> WatchedDirectories()
    {
        var directories = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var entry in BackupCatalog.Entries)
        {
            string source = BackupCatalog.ResolveSource(_service.RootDirectory, entry);
            string? directory = entry.IsDirectory ? source : Path.GetDirectoryName(source);
            if (!string.IsNullOrEmpty(directory)) directories.Add(directory);
        }

        var kept = new List<string>();
        foreach (var directory in directories.OrderBy(d => d.Length))
        {
            if (!kept.Any(existing => IsSameOrUnderDirectory(directory, existing))) kept.Add(directory);
        }

        return kept;
    }

    private static bool IsSameOrUnderDirectory(string candidate, string ancestor)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidate, ancestor, comparison)) return true;

        string ancestorPrefix = ancestor.EndsWith(Path.DirectorySeparatorChar) ? ancestor : ancestor + Path.DirectorySeparatorChar;
        return candidate.StartsWith(ancestorPrefix, comparison);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => NotifyFileSystemEvent(e.FullPath);

    /// <summary>
    /// The filter logic <see cref="OnFileSystemEvent"/> runs on every real watcher callback,
    /// exposed as its own seam so tests can drive it directly instead of waiting on a real
    /// <see cref="FileSystemWatcher"/> — event timing is not deterministic enough to assert on,
    /// and CI's ubuntu runners hit inotify limits.
    ///
    /// A positive allowlist (I2): only a path that is itself, or falls under, one of the real
    /// backed-up <see cref="BackupCatalog.Entries"/> ever wakes the debounce. WatchedDirectories()
    /// resolves RootDirectory itself with <see cref="FileSystemWatcher.IncludeSubdirectories"/>
    /// true, so every file under the app data root reaches this method — that includes both our
    /// own machinery (backups/, the .import-&lt;guid&gt;/ scratch tree) and, more importantly,
    /// continuously-appended files that are simply never backed up at all
    /// (logs/debug.log, command-assist/history.jsonl). A denylist that only named the former left
    /// the latter free to keep the debounce from ever elapsing during active use — an
    /// <c>auto</c> snapshot effectively never fired — and, if a snapshot write itself kept
    /// failing, produced a self-retrigger loop (each failure logs into logs/debug.log, which sat
    /// inside the watched tree and woke the debounce again). Backed-up paths only, checked here
    /// against <see cref="BackupCatalog.IsBackedUpPath"/>, excludes both problems at once: neither
    /// a log file nor our own scratch/output directories are ever a real catalog path.
    /// </summary>
    internal void NotifyFileSystemEvent(string fullPath)
    {
        string relative = Path.GetRelativePath(_service.RootDirectory, fullPath);
        if (!BackupCatalog.IsBackedUpPath(relative)) return;

        NotifyChanged();
    }

    /// <summary>Test seam mirroring the debounce's internal pending flag.</summary>
    internal bool HasPendingChange => _pending;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var watcher in _watchers)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
        }

        _watchers.Clear();

        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }

        // Best-effort final flush (I4): a change debounced but not yet fired must not be
        // silently dropped just because the timer was torn down above without ever firing.
        // Only attempted when the gate is free right now (Wait(0), non-blocking): if a flush is
        // already in flight — e.g. the timer fired a moment before this call and FlushAsync is
        // mid-Snapshot on another thread — that flush will complete the pending change on its
        // own. Blocking here to wait it out (or unconditionally touching _pending/Snapshot
        // concurrently with it) would reintroduce exactly the "Dispose races an in-flight flush"
        // hazard already fixed in FlushAsync's own finally block — see its remarks and
        // Flush_SurvivesDisposeRacingAnInFlightSnapshot.
        if (_pending && _gate.Wait(0))
        {
            try
            {
                bool stillPending;
                lock (_timerLock)
                {
                    stillPending = _pending;
                    _pending = false;
                    _pendingSince = null;
                }

                // Snapshot() never throws (see its own remarks) - it logs and returns null on
                // failure, so no extra try/catch is needed around this call.
                if (stillPending) _service.Snapshot(SnapshotReason.Auto);
            }
            finally
            {
                _gate.Release();
            }
        }

        _gate.Dispose();
    }
}
