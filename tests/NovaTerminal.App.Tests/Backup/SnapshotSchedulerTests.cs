using NovaTerminal.Backup;

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

        Assert.Null(Record.Exception(() => scheduler.Start()));
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
    /// I2 fix round: the filter changed from a denylist (a couple of known-noisy prefixes) to a
    /// positive allowlist (only real backed-up paths). "backups-legacy" was previously the
    /// regression test for the denylist's prefix anchoring (it contains "backups" as a bare
    /// substring but is not the real snapshot directory); under the allowlist it is simply not a
    /// catalog path at all — same conclusion (not our own machinery) reached without needing any
    /// prefix-anchoring logic in the first place.
    /// </summary>
    [Fact]
    public void SimilarlyNamedDirectory_DoesNotMarkAChangePending()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyFileSystemEvent(Path.Combine(tree.Root, "backups-legacy", "whatever.json"));

        Assert.False(scheduler.HasPendingChange);
    }

    /// <summary>
    /// I2's core regression: logs/debug.log is appended continuously by AppLogger.Log during
    /// active use, and sits inside the watched tree (RootDirectory, IncludeSubdirectories: true).
    /// It is not, and never has been, a backed-up path — but the old denylist only named
    /// "backups" and ".import-", so a write here used to mark a change pending and kept the
    /// debounce from ever elapsing. The positive allowlist rejects it outright.
    /// </summary>
    [Fact]
    public void WriteToLogFile_DoesNotMarkAChangePending()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyFileSystemEvent(Path.Combine(tree.Root, "logs", "debug.log"));

        Assert.False(scheduler.HasPendingChange);
    }

    /// <summary>
    /// Same regression as <see cref="WriteToLogFile_DoesNotMarkAChangePending"/>, for the other
    /// continuously-written, never-backed-up file called out in I2: command history.
    /// </summary>
    [Fact]
    public void WriteToCommandHistory_DoesNotMarkAChangePending()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.NotifyFileSystemEvent(Path.Combine(tree.Root, "command-assist", "history.jsonl"));

        Assert.False(scheduler.HasPendingChange);
    }

    /// <summary>
    /// I2's second half: a continuously-busy tree must still get a snapshot eventually. Each
    /// NotifyChanged() call resets the trailing debounce, so without a cap a tree that is never
    /// quiet for a full debounce window would never schedule a flush at all. Driven through the
    /// injected TimeProvider (no real waiting): the first change schedules the full debounce:
    /// the clock then advances close to the max-delay boundary and a second change schedules
    /// only the remaining budget, less than a full debounce; advancing past the boundary
    /// entirely collapses the next scheduled delay to zero, forcing an effectively-immediate
    /// flush regardless of how many changes keep arriving.
    /// </summary>
    [Fact]
    public void NotifyChanged_ContinuousChanges_EventuallyCapAtZeroDelay()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
        var service = new BackupService(tree.Root, clock);
        using var scheduler = new SnapshotScheduler(
            service,
            debounce: TimeSpan.FromSeconds(30),
            maxDelay: TimeSpan.FromMinutes(2),
            timeProvider: clock);

        scheduler.NotifyChanged();
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.LastScheduledDelayForTest);

        clock.Advance(TimeSpan.FromSeconds(110)); // within the 2-minute cap, but close to it
        scheduler.NotifyChanged();
        Assert.True(scheduler.LastScheduledDelayForTest < TimeSpan.FromSeconds(30));
        Assert.True(scheduler.LastScheduledDelayForTest > TimeSpan.Zero);

        clock.Advance(TimeSpan.FromSeconds(15)); // now past the 2-minute cap
        scheduler.NotifyChanged();
        Assert.Equal(TimeSpan.Zero, scheduler.LastScheduledDelayForTest);
    }

    /// <summary>
    /// I4: a change debounced but not yet fired must not be silently dropped just because the
    /// process is quitting. A long debounce here proves the timer never fires on its own within
    /// the test - the snapshot on disk can only be explained by Dispose's own best-effort flush.
    /// </summary>
    [Fact]
    public void Dispose_WithPendingChange_FlushesBeforeTearingDown()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        var scheduler = new SnapshotScheduler(service, TimeSpan.FromMinutes(5));

        scheduler.NotifyChanged();
        Assert.True(scheduler.HasPendingChange);

        scheduler.Dispose();

        Assert.Single(service.ListSnapshots());
    }

    /// <summary>
    /// The common case must stay a no-op: disposing with nothing pending must not write a
    /// spurious snapshot.
    /// </summary>
    [Fact]
    public void Dispose_WithNoPendingChange_WritesNoSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        var scheduler = new SnapshotScheduler(service, TimeSpan.FromMinutes(5));

        scheduler.Dispose();

        Assert.Empty(service.ListSnapshots());
    }

    private static TimeProvider Clock() =>
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}
