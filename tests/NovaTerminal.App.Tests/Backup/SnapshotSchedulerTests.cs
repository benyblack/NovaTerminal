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

    [Fact]
    public async Task Flush_CoalescesManyChangesIntoOneSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        using var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        for (int i = 0; i < 10; i++) scheduler.NotifyChanged();
        await scheduler.FlushAsync();

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
        var second = await scheduler.FlushAsync();

        Assert.Null(second);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());
        var scheduler = new SnapshotScheduler(service, TimeSpan.FromMilliseconds(10));

        scheduler.Dispose();
        scheduler.Dispose();
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

    private static TimeProvider Clock() =>
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}
