using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class SnapshotSchedulerTests
{
    [Fact]
    public async Task Flush_WritesSnapshotWhenChangePending()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyChanged();
        var info = await scheduler.FlushAsync();

        Assert.NotNull(info);
        Assert.Single(service.ListSnapshots());
    }

    [Fact]
    public async Task Flush_WithoutChange_WritesNothing()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        var info = await scheduler.FlushAsync();

        Assert.Null(info);
        Assert.Empty(service.ListSnapshots());
    }

    /// <summary>
    /// Pins the actual coalescing property, not just "one snapshot exists" (which a boolean
    /// pending flag satisfies trivially, debounced or not). A long debounce keeps the background
    /// timer from firing mid-test — FlushAsync is driven explicitly here, so the debounce length
    /// itself is irrelevant to what this test checks. Each notify follows a genuine content
    /// change, so a broken (non-coalescing) implementation that flushed once per notify would
    /// produce 10 distinct snapshots — the content-hash dedupe could not mask that, because the
    /// content really did change each time. Only a correctly debounced scheduler collapses these
    /// into the single flush below.
    /// </summary>
    [Fact]
    public async Task Flush_CoalescesManyChangesIntoOneSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromSeconds(30));

        for (int i = 0; i < 10; i++)
        {
            tree.WriteFile(Path.Combine("themes", "solarized.json"), $$"""{"name":"Solarized","revision":{{i}}}""");
            scheduler.NotifyChanged();
            Assert.True(scheduler.HasPendingChange);
        }

        var info = await scheduler.FlushAsync();

        Assert.NotNull(info);
        Assert.False(scheduler.HasPendingChange);
        Assert.Single(service.ListSnapshots());
    }

    [Fact]
    public async Task Flush_ClearsPendingFlag()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyChanged();
        await scheduler.FlushAsync();

        // Pin the flag directly: BackupService.Snapshot(Auto) also returns null when its own
        // content-hash dedupe fires, so asserting only "second flush returns null" would pass
        // even for a scheduler that never clears _pending.
        Assert.False(scheduler.HasPendingChange);

        var second = await scheduler.FlushAsync();
        Assert.Null(second);
    }

    /// <summary>
    /// Exercises real teardown (Start() registers actual watchers first) and proves the second
    /// Dispose() is not merely non-throwing but actually a no-op: with the guard removed,
    /// disposing watchers/timer a second time still would not throw (each underlying Dispose is
    /// independently idempotent), so a bare double-Dispose-doesn't-throw test cannot fail. Calling
    /// NotifyChanged() afterward and asserting no pending change pins the _disposed guard itself.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotentAndStopsFurtherWork()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));
        scheduler.Start();

        scheduler.Dispose();
        scheduler.Dispose();

        scheduler.NotifyChanged();
        Assert.False(scheduler.HasPendingChange);
    }

    /// <summary>
    /// Deterministically reproduces Dispose() racing an in-flight flush without relying on real
    /// thread timing: BeforeSnapshotForTest runs synchronously on FlushAsync's own call stack,
    /// right after the gate is acquired and the pending flag cleared, but before
    /// BackupService.Snapshot() runs. Disposing there simulates the exact window where a
    /// concurrent Dispose() could tear down the gate while this call still holds it. Before the
    /// fix, the finally block's _gate.Release() would throw ObjectDisposedException and replace
    /// the successfully computed SnapshotInfo with a fault; this asserts the caller still gets it.
    /// </summary>
    [Fact]
    public async Task Flush_SurvivesDisposeRacingAnInFlightSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyChanged();
        scheduler.BeforeSnapshotForTest = () => scheduler.Dispose();

        var info = await scheduler.FlushAsync();

        Assert.NotNull(info);
        Assert.Single(service.ListSnapshots());
    }

    [Fact]
    public void Start_OnMissingDirectories_DoesNotThrow()
    {
        using var tree = BackupTestTree.CreateEmpty();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.Start();
    }

    /// <summary>
    /// The scheduler must ignore its own machinery. Both live under RootDirectory — snapshots in
    /// backups/, and the import scratch tree in .import-&lt;guid&gt;/ (it sits beside the live tree
    /// rather than in TEMP because Directory.Move throws across a volume boundary). The watcher
    /// covers RootDirectory with IncludeSubdirectories = true, so without the filter an import
    /// would wake the debounce on every file it stages.
    /// </summary>
    [Theory]
    [InlineData("backups")]
    [InlineData(".import-abc123")]
    public void SelfWrites_DoNotMarkAChangePending(string selfDirectory)
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyFileSystemEvent(Path.Combine(tree.Root, selfDirectory, "whatever.json"));

        Assert.False(scheduler.HasPendingChange);
    }

    [Fact]
    public void RealConfigWrite_MarksAChangePending()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyFileSystemEvent(Path.Combine(tree.Root, "themes", "solarized.json"));

        Assert.True(scheduler.HasPendingChange);
    }

    /// <summary>
    /// The backups filter must be anchored on a trailing separator, not a bare substring match:
    /// "backups-legacy" contains "backups" but is not our snapshot directory, so it must still be
    /// treated as a real config write.
    /// </summary>
    [Fact]
    public void SimilarlyNamedDirectory_MarksAChangePending()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyFileSystemEvent(Path.Combine(tree.Root, "backups-legacy", "whatever.json"));

        Assert.True(scheduler.HasPendingChange);
    }

    private static TimeProvider Clock() =>
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}
