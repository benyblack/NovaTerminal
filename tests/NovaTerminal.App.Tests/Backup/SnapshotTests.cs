using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class SnapshotTests
{
    [Fact]
    public void Snapshot_WritesBundleIntoBackupsDirectory()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        var info = service.Snapshot(SnapshotReason.Auto);

        Assert.NotNull(info);
        Assert.StartsWith("auto-20260827T091400Z-", info!.Id);
        Assert.True(File.Exists(info.FilePath));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(tree.Root, "backups")),
            Path.GetFullPath(Path.GetDirectoryName(info.FilePath)!));
    }

    /// <summary>
    /// Pins the dedupe hash width at 16 hex chars (64 bits). A regression back to 8 hex chars
    /// (32 bits) would still pass every other test here — dedupe is only ever exercised with a
    /// handful of distinct contents per test, far too few to hit a 32-bit collision — but it
    /// would leave production dedupe with a real, if small, chance of silently skipping a
    /// snapshot for genuinely changed content. This test fails immediately on that regression
    /// instead of relying on a collision to happen to occur.
    /// </summary>
    [Fact]
    public void Snapshot_IdHashSegmentIsSixteenHexChars()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());

        var info = service.Snapshot(SnapshotReason.Auto);

        string hashSegment = info!.Id[(info.Id.LastIndexOf('-') + 1)..];
        Assert.Equal(16, hashSegment.Length);
        Assert.Matches("^[0-9a-f]{16}$", hashSegment);
        Assert.Equal(hashSegment, info.ContentHash);
    }

    [Fact]
    public void Snapshot_ReasonEncodedInIdPrefix()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());

        Assert.StartsWith("pre-import-", service.Snapshot(SnapshotReason.PreImport)!.Id);
        Assert.StartsWith("pre-restore-", service.Snapshot(SnapshotReason.PreRestore)!.Id);
    }

    [Fact]
    public void Snapshot_Auto_SkipsWhenContentUnchanged()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        var first = service.Snapshot(SnapshotReason.Auto);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = service.Snapshot(SnapshotReason.Auto);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(service.ListSnapshots());
    }

    [Fact]
    public void Snapshot_Auto_WritesWhenContentChanged()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":22}""");
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = service.Snapshot(SnapshotReason.Auto);

        Assert.NotNull(second);
        Assert.Equal(2, service.ListSnapshots().Count);
    }

    /// <summary>
    /// A forced snapshot records the pre-state of a destructive operation even when that state
    /// is byte-identical to the newest auto snapshot. Deduping it away would mean a failed
    /// import had no snapshot of its own to point the user at.
    /// </summary>
    [Fact]
    public void Snapshot_Forced_IgnoresDedupe()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        service.Snapshot(SnapshotReason.Auto);
        clock.Advance(TimeSpan.FromMinutes(1));
        var forced = service.Snapshot(SnapshotReason.PreImport);

        Assert.NotNull(forced);
        Assert.Equal(2, service.ListSnapshots().Count);
    }

    [Fact]
    public void ListSnapshots_ReturnsNewestFirstWithParsedMetadata()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":22}""");
        clock.Advance(TimeSpan.FromHours(1));
        service.Snapshot(SnapshotReason.Auto);

        var snapshots = service.ListSnapshots();

        Assert.Equal(2, snapshots.Count);
        Assert.True(snapshots[0].CreatedUtc > snapshots[1].CreatedUtc);
        Assert.All(snapshots, s => Assert.True(s.SizeBytes > 0));
        Assert.All(snapshots, s => Assert.Equal(SnapshotReason.Auto, s.Reason));
    }

    /// <summary>
    /// The snapshot id is `&lt;reason&gt;-&lt;timestamp&gt;-&lt;hash16&gt;`, and the reason token
    /// itself may contain a dash (`pre-import`, `pre-restore`). A parser that naively splits on
    /// every dash, or splits from the left, mis-parses those two and either drops the snapshot
    /// from the list or reports the wrong reason. Covering all three reasons through the real
    /// parse path (ListSnapshots, not just the id prefix from Snapshot's return value) is what
    /// catches that.
    /// </summary>
    [Fact]
    public void ListSnapshots_RoundTripsAllThreeReasons()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        service.Snapshot(SnapshotReason.Auto);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Snapshot(SnapshotReason.PreImport);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Snapshot(SnapshotReason.PreRestore);

        var snapshots = service.ListSnapshots();

        Assert.Equal(3, snapshots.Count);
        Assert.Contains(snapshots, s => s.Reason == SnapshotReason.Auto);
        Assert.Contains(snapshots, s => s.Reason == SnapshotReason.PreImport);
        Assert.Contains(snapshots, s => s.Reason == SnapshotReason.PreRestore);
    }

    [Fact]
    public void Retention_KeepsNewestTwentyWhenAllAreOld()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        for (int i = 0; i < 25; i++)
        {
            tree.WriteFile("settings.json", $$"""{"FontSize":{{i}}}""");
            clock.Advance(TimeSpan.FromDays(30)); // every snapshot ages out of the 7-day window
            service.Snapshot(SnapshotReason.Auto);
        }

        Assert.Equal(BackupService.MaxSnapshots, service.ListSnapshots().Count);
    }

    [Fact]
    public void Retention_KeepsMoreThanTwentyWhenAllAreRecent()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        for (int i = 0; i < 25; i++)
        {
            tree.WriteFile("settings.json", $$"""{"FontSize":{{i}}}""");
            clock.Advance(TimeSpan.FromMinutes(1)); // all inside the 7-day window
            service.Snapshot(SnapshotReason.Auto);
        }

        Assert.Equal(25, service.ListSnapshots().Count);
    }

    [Fact]
    public void Snapshot_NeverContainsAnotherSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":22}""");
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = service.Snapshot(SnapshotReason.Auto);

        using var zip = System.IO.Compression.ZipFile.OpenRead(second!.FilePath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("backups", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".novabackup", StringComparison.Ordinal));
    }

    private static FixedTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}
