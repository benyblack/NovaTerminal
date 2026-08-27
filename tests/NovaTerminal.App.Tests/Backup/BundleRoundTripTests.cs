using System.IO.Compression;
using System.Text;
using NovaTerminal.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BundleRoundTripTests
{
    [Fact]
    public void Write_ProducesManifestAndCategoryEntries()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "out.novabackup");

        BundleWriter.Write(tree.Root, bundle, BackupCatalog.AllCategories, NewManifest());

        using var zip = ZipFile.OpenRead(bundle);
        var names = zip.Entries.Select(e => e.FullName).ToArray();

        Assert.Contains("manifest.json", names);
        Assert.Contains("settings/settings.json", names);
        Assert.Contains("themes/solarized.json", names);
        Assert.Contains("connections/profiles.json", names);
        Assert.Contains("command-assist/snippets.json", names);
    }

    [Fact]
    public void Write_OmitsExcludedContent()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "out.novabackup");

        BundleWriter.Write(tree.Root, bundle, BackupCatalog.AllCategories, NewManifest());

        using var zip = ZipFile.OpenRead(bundle);
        var names = zip.Entries.Select(e => e.FullName).ToArray();

        Assert.DoesNotContain(names, n => n.StartsWith("logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("recordings/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("history.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_HonorsCategorySubset()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "themes-only.novabackup");

        BundleWriter.Write(tree.Root, bundle, new[] { BackupCategory.Themes }, NewManifest());

        using var zip = ZipFile.OpenRead(bundle);
        var names = zip.Entries.Select(e => e.FullName).ToArray();

        Assert.Contains("themes/solarized.json", names);
        Assert.DoesNotContain("settings/settings.json", names);
    }

    [Fact]
    public void Open_ReturnsManifestAndItemCounts()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "out.novabackup");
        BundleWriter.Write(tree.Root, bundle, BackupCatalog.AllCategories, NewManifest());

        var outcome = BundleReader.Open(bundle);

        Assert.True(outcome.Success, outcome.Message);
        Assert.NotNull(outcome.Inspection);
        Assert.Equal(BackupManifest.CurrentSchemaVersion, outcome.Inspection!.Manifest.SchemaVersion);
        Assert.Equal("1.0.0-test", outcome.Inspection.Manifest.AppVersion);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero), outcome.Inspection.Manifest.CreatedUtc);
        Assert.Equal("TEST", outcome.Inspection.Manifest.Machine);
        Assert.Equal(
            BackupCatalog.AllCategories.Select(c => c.ToString().ToLowerInvariant()),
            outcome.Inspection.Manifest.Categories);
        Assert.Equal(1, outcome.Inspection.ItemCounts[BackupCategory.Themes]);
        Assert.Equal(2, outcome.Inspection.ItemCounts[BackupCategory.Connections]);
    }

    [Fact]
    public void Open_WithCategorySubset_SucceedsAndCountsOnlyWrittenCategories()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "themes-only.novabackup");
        var manifest = NewManifest() with { Categories = new[] { "themes" } };

        BundleWriter.Write(tree.Root, bundle, new[] { BackupCategory.Themes }, manifest);

        var outcome = BundleReader.Open(bundle);

        Assert.True(outcome.Success, outcome.Message);
        Assert.NotNull(outcome.Inspection);
        Assert.Equal(new[] { "themes" }, outcome.Inspection!.Manifest.Categories);
        Assert.Equal(1, outcome.Inspection.ItemCounts[BackupCategory.Themes]);
        Assert.Equal(0, outcome.Inspection.ItemCounts[BackupCategory.Connections]);
    }

    [Fact]
    public void ExtractTo_ReproducesSourceContent()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreateEmpty();
        string bundle = Path.Combine(source.Root, "out.novabackup");
        BundleWriter.Write(source.Root, bundle, BackupCatalog.AllCategories, NewManifest());

        BundleReader.ExtractTo(bundle, target.Root, BackupCatalog.AllCategories);

        Assert.Equal(source.ReadFile("settings.json"), target.ReadFile("settings.json"));
        Assert.Equal(
            source.ReadFile(Path.Combine("themes", "solarized.json")),
            target.ReadFile(Path.Combine("themes", "solarized.json")));
        Assert.Equal(
            source.ReadFile(Path.Combine("ssh", "profiles.json")),
            target.ReadFile(Path.Combine("ssh", "profiles.json")));
    }

    [Fact]
    public void ExtractTo_RejectsEntryThatEscapesUpward()
    {
        using var stage = BackupTestTree.CreateEmpty();
        string bundle = Path.Combine(stage.Root, "malicious.novabackup");
        WriteBundleWithRawEntry(bundle, "themes/../../evil.txt", "payload");

        string destinationRoot = Path.Combine(stage.Root, "dest");
        Directory.CreateDirectory(destinationRoot);

        Assert.Throws<InvalidDataException>(() =>
            BundleReader.ExtractTo(bundle, destinationRoot, new[] { BackupCategory.Themes }));

        // Two "../" from destinationRoot/themes lands in stage.Root — assert nothing was written there.
        string escapedFile = Path.Combine(stage.Root, "evil.txt");
        Assert.False(File.Exists(escapedFile));
    }

    [Fact]
    public void ExtractTo_RejectsSamePrefixSiblingEscape()
    {
        // Regression test for the bare-StartsWith zip-slip bypass: a destination whose full path
        // begins with the same characters as the root, but is actually an unrelated sibling
        // directory, must still be rejected.
        using var stage = BackupTestTree.CreateEmpty();
        string parent = Path.Combine(stage.Root, "parent");
        Directory.CreateDirectory(parent);
        string destinationRoot = Path.Combine(parent, "root");
        Directory.CreateDirectory(destinationRoot);

        string bundle = Path.Combine(stage.Root, "malicious-sibling.novabackup");
        // "rootEvil" starts with "root" as a string — this is exactly what
        // fullDestination.StartsWith(fullRoot) let through before the fix.
        WriteBundleWithRawEntry(bundle, "themes/../../rootEvil/evil.txt", "payload");

        Assert.Throws<InvalidDataException>(() =>
            BundleReader.ExtractTo(bundle, destinationRoot, new[] { BackupCategory.Themes }));

        string escapedFile = Path.Combine(parent, "rootEvil", "evil.txt");
        Assert.False(File.Exists(escapedFile));
    }

    [Fact]
    public void Open_RejectsNonZipFile()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string bogus = Path.Combine(tree.Root, "not-a-zip.novabackup");
        File.WriteAllText(bogus, "this is plain text");

        var outcome = BundleReader.Open(bogus);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.CorruptArchive, outcome.Failure);
    }

    [Fact]
    public void Open_RejectsTruncatedZip()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "out.novabackup");
        BundleWriter.Write(tree.Root, bundle, BackupCatalog.AllCategories, NewManifest());

        // Lop off the central directory — the classic half-copied-file case.
        byte[] full = File.ReadAllBytes(bundle);
        File.WriteAllBytes(bundle, full[..(full.Length / 2)]);

        var outcome = BundleReader.Open(bundle);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.CorruptArchive, outcome.Failure);
    }

    [Fact]
    public void Open_RejectsMissingFile()
    {
        using var tree = BackupTestTree.CreateEmpty();

        var outcome = BundleReader.Open(Path.Combine(tree.Root, "nope.novabackup"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.NotFound, outcome.Failure);
    }

    [Fact]
    public void Open_RejectsZipWithoutManifest()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string bundle = Path.Combine(tree.Root, "no-manifest.novabackup");
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("settings/settings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }

        var outcome = BundleReader.Open(bundle);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.NotABackup, outcome.Failure);
    }

    [Fact]
    public void Open_RejectsMalformedManifest()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string bundle = Path.Combine(tree.Root, "bad-manifest.novabackup");
        WriteManifestOnlyBundle(bundle, "{ this is not json");

        var outcome = BundleReader.Open(bundle);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.NotABackup, outcome.Failure);
    }

    [Fact]
    public void Open_RejectsNewerSchemaVersion()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string bundle = Path.Combine(tree.Root, "future.novabackup");
        int future = BackupManifest.CurrentSchemaVersion + 1;
        WriteManifestOnlyBundle(
            bundle,
            $$"""{"schemaVersion":{{future}},"appVersion":"9.9.9","createdUtc":"2030-01-01T00:00:00+00:00","machine":"FUTURE","categories":["settings"]}""");

        var outcome = BundleReader.Open(bundle);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.UnsupportedSchemaVersion, outcome.Failure);
        Assert.Contains(future.ToString(), outcome.Message);
    }

    [Fact]
    public void Open_RejectsManifestCategoryWithNoContent()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string bundle = Path.Combine(tree.Root, "corrupt.novabackup");
        WriteManifestOnlyBundle(
            bundle,
            """{"schemaVersion":1,"appVersion":"1.0.0","createdUtc":"2026-08-27T00:00:00+00:00","machine":"X","categories":["settings"]}""");

        var outcome = BundleReader.Open(bundle);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.MissingCategoryContent, outcome.Failure);
    }

    private static BackupManifest NewManifest() => new()
    {
        SchemaVersion = BackupManifest.CurrentSchemaVersion,
        AppVersion = "1.0.0-test",
        CreatedUtc = new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero),
        Machine = "TEST",
        Categories = BackupCatalog.AllCategories.Select(c => c.ToString().ToLowerInvariant()).ToArray()
    };

    private static void WriteManifestOnlyBundle(string path, string manifestJson)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.json");
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(manifestJson));
    }

    /// <summary>
    /// Builds a zip with a single hand-crafted entry name and no manifest — for exercising
    /// <see cref="BundleReader.ExtractTo"/> directly against a malicious archive.
    /// </summary>
    private static void WriteBundleWithRawEntry(string path, string entryName, string contents)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(contents));
    }
}
