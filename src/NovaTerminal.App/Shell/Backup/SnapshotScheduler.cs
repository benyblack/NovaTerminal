using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Shell.Backup;

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

    private readonly BackupService _service;
    private readonly TimeSpan _debounce;
    private readonly string _backupsPrefix;
    private readonly Action<string> _log;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();

    private Timer? _timer;
    private bool _pending;
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

    public SnapshotScheduler(BackupService service, TimeSpan? debounce = null, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _debounce = debounce ?? DefaultDebounce;
        _log = log ?? (static _ => { });
        // Anchored with a trailing separator so a sibling like "backups2/" or "backups-legacy/"
        // is never mistaken for our own snapshot directory.
        _backupsPrefix = Path.Combine(_service.RootDirectory, "backups") + Path.DirectorySeparatorChar;
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

    /// <summary>Marks a change pending and (re)starts the debounce timer.</summary>
    public void NotifyChanged()
    {
        if (_disposed) return;

        lock (_timerLock)
        {
            _pending = true;
            _timer ??= new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
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
    /// Never let the scheduler re-trigger on our own writes. Two sources:
    ///  - backups/       snapshot bundles written by Snapshot()
    ///  - .import-&lt;guid&gt;/ the import scratch tree (extracted/, final/, undo/). It lives beside
    ///    the live tree rather than in TEMP because Directory.Move is a bare rename and throws
    ///    across a volume boundary — so it IS under RootDirectory and the watcher does see it.
    /// WatchedDirectories() resolves RootDirectory itself (it is settings.json's parent) with
    /// IncludeSubdirectories = true, so without this both would wake the debounce on every file.
    /// </summary>
    internal void NotifyFileSystemEvent(string fullPath)
    {
        if (fullPath.Contains(_backupsPrefix, StringComparison.Ordinal)) return;
        if (fullPath.Contains($"{Path.DirectorySeparatorChar}.import-", StringComparison.Ordinal)) return;

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

        _gate.Dispose();
    }
}
