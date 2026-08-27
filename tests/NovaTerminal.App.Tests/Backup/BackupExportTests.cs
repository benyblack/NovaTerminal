using System.IO.Compression;
using System.Text;
using NovaTerminal.Platform.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupExportTests
{
    [Fact]
    public void Export_WritesBundleWithAllCategoriesByDefault()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "export.novabackup");

        var outcome = service.Export(bundle);

        Assert.True(outcome.Success, outcome.Message);
        Assert.True(File.Exists(bundle));

        var inspection = service.Inspect(bundle);
        Assert.True(inspection.Success, inspection.Message);
        Assert.Equal(
            BackupCatalog.AllCategories.Count,
            inspection.Inspection!.Manifest.Categories.Count);
    }

    [Fact]
    public void Export_StampsManifestFromClock()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "export.novabackup");

        service.Export(bundle);
        var inspection = service.Inspect(bundle);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero),
            inspection.Inspection!.Manifest.CreatedUtc);
        Assert.False(string.IsNullOrWhiteSpace(inspection.Inspection.Manifest.AppVersion));
        Assert.False(string.IsNullOrWhiteSpace(inspection.Inspection.Manifest.Machine));
    }

    [Fact]
    public void Export_SubsetOmitsOtherCategories()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "subset.novabackup");

        service.Export(bundle, new[] { BackupCategory.Themes, BackupCategory.Snippets });
        var inspection = service.Inspect(bundle);

        Assert.Equal(
            new[] { "themes", "snippets" }.Order().ToArray(),
            inspection.Inspection!.Manifest.Categories.Order().ToArray());
        Assert.Equal(0, inspection.Inspection.ItemCounts[BackupCategory.Settings]);
    }

    /// <summary>
    /// Structural guarantee for issue #100: a bundle must never carry secret material.
    /// The sentinel is planted in every place a naive implementation might pick it up.
    /// </summary>
    [Fact]
    public void Export_NeverContainsSecretMaterial()
    {
        const string sentinel = "hunter2-SUPER-SECRET-SENTINEL";
        using var tree = BackupTestTree.CreatePopulated();
        tree.WriteFile("vault.dat", sentinel);
        tree.WriteFile(Path.Combine("command-assist", "history.jsonl"), $$"""{"cmd":"echo {{sentinel}}"}""");
        tree.WriteFile(Path.Combine("logs", "debug.log"), sentinel);

        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "export.novabackup");
        service.Export(bundle);

        byte[] bytes = File.ReadAllBytes(bundle);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(sentinel), bytes);

        // Also assert on decompressed content — compression could hide the raw bytes.
        using var zip = ZipFile.OpenRead(bundle);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain(sentinel, reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Export_SucceedsWhenSomeCategoriesAreAbsent()
    {
        using var tree = BackupTestTree.CreateEmpty();
        tree.WriteFile("settings.json", """{"FontSize":16}""");
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "sparse.novabackup");

        var outcome = service.Export(bundle);

        Assert.True(outcome.Success, outcome.Message);
        var inspection = service.Inspect(bundle);
        Assert.True(inspection.Success, inspection.Message);
        Assert.Equal(new[] { "settings" }, inspection.Inspection!.Manifest.Categories);
    }

    [Fact]
    public void Export_FailsGracefullyWhenDestinationUnwritable()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        // A directory parked on the destination path blocks the file write on every OS.
        string blocked = Path.Combine(tree.Root, "blocked.novabackup");
        Directory.CreateDirectory(blocked);

        var outcome = service.Export(blocked);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    [Fact]
    public void Export_FailsGracefullyForEmptyDestination()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export("");

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    [Fact]
    public void Export_FailsGracefullyForWhitespaceDestination()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export("   ");

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// An embedded NUL is rejected by Path.GetFullPath on every platform .NET supports
    /// (unlike other "invalid" characters such as '&lt;' or '|', which are only rejected on
    /// Windows and only much later, by the filesystem) — so this is the one malformed-path
    /// case that behaves identically on Windows and POSIX.
    /// </summary>
    [Fact]
    public void Export_FailsGracefullyForPathWithEmbeddedNul()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string malformed = Path.Combine(tree.Root, "bad\0name.novabackup");

        var outcome = service.Export(malformed);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    private static TimeProvider FixedClock() =>
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}

/// <summary>Deterministic clock so manifest timestamps and snapshot ids are assertable.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
