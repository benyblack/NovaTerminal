using System.IO.Compression;
using System.Text;
using NovaTerminal.Backup;

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

    /// <summary>
    /// F2 (Codex review, PR #362): a destination that resolves onto a live catalog FILE (not a
    /// directory entry) is the sharpest case - BundleWriter would archive the current
    /// settings.json into its temp bundle, then <c>File.Move(overwrite: true)</c> replaces the
    /// live file with ZIP bytes, and Export used to still report success. Asserts both that Export
    /// now fails AND that the live file's content survives untouched - the second assertion is
    /// what actually catches a regression back to the old "reject the intent, still clobber the
    /// file first" bug were the guard placed after BundleWriter ran instead of before it.
    /// </summary>
    [Fact]
    public void Export_ToLiveSettingsFile_IsRejected_AndLeavesTheLiveFileIntact()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string original = tree.ReadFile("settings.json");
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(Path.Combine(tree.Root, "settings.json"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Equal(original, tree.ReadFile("settings.json"));
    }

    /// <summary>
    /// Same aliasing hazard, for a directory-shaped catalog entry (Themes): a destination nested
    /// under "themes/" is still "at-or-under" the catalog source per the finding's wording, even
    /// though it does not exactly equal the directory itself.
    ///
    /// Requests only the Settings category (excluding Themes) so <c>BundleWriter</c> never
    /// enumerates the themes directory itself - otherwise its own temp-sibling-then-move write
    /// (the temp file briefly lives right next to the destination) would pick up its own
    /// in-progress temp file mid-enumeration and fail with an unrelated <c>IOException</c>
    /// regardless of this guard, defeating the point of a targeted regression test. The guard
    /// under test here must fire from a full walk of <c>BackupCatalog.Entries</c>, independent of
    /// which categories were actually requested.
    /// </summary>
    [Fact]
    public void Export_ToPathUnderLiveThemesDirectory_IsRejected()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(
            Path.Combine(tree.Root, "themes", "sneaky.novabackup"),
            new[] { BackupCategory.Settings });

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.False(File.Exists(Path.Combine(tree.Root, "themes", "sneaky.novabackup")));
    }

    /// <summary>
    /// The connections/native_known_hosts.json catalog entry, exercised separately from
    /// settings.json so the guard is proven to walk every <c>BackupCatalog.Entries</c> row, not
    /// just the first one checked.
    /// </summary>
    [Fact]
    public void Export_ToLiveConnectionsFile_IsRejected()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(Path.Combine(tree.Root, "ssh", "native_known_hosts.json"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// Design decision (documented in the fix's own remarks on
    /// <c>BackupService.TryDescribeProtectedDestination</c>): <see cref="BackupService.BackupsDirectory"/>
    /// is rejected too, even though it is not itself a <c>BackupCatalog.Entries</c> source. It is
    /// where <c>Snapshot</c> writes the pre-import/pre-restore rollback points <c>Restore</c>
    /// depends on; an Export landing on an existing snapshot's file name would silently destroy
    /// that rollback point the same way an aliased catalog entry destroys live configuration.
    /// </summary>
    [Fact]
    public void Export_IntoBackupsDirectory_IsRejected()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        Directory.CreateDirectory(service.BackupsDirectory);

        var outcome = service.Export(Path.Combine(service.BackupsDirectory, "sneaky.novabackup"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// A destination that merely shares a name PREFIX with a catalog entry (rather than being the
    /// entry itself or nested under it) must still be allowed - "settings.json.export" is not
    /// "settings.json", and a naive <c>StartsWith</c> without a separator boundary would wrongly
    /// reject it.
    /// </summary>
    [Fact]
    public void Export_ToPathWithNamePrefixOfCatalogEntry_IsStillAllowed()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(Path.Combine(tree.Root, "settings.json.export"));

        Assert.True(outcome.Success, outcome.Message);
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
