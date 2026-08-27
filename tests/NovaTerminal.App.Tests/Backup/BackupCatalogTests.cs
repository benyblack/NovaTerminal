using System.Reflection;
using NovaTerminal.Shell;
using NovaTerminal.Platform.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupCatalogTests
{
    private const string CommandAssistRelativeDirectory = "command-assist";

    [Fact]
    public void Entries_CoverEveryCategory()
    {
        foreach (var category in BackupCatalog.AllCategories)
        {
            Assert.NotEmpty(BackupCatalog.EntriesFor(category));
        }
    }

    [Fact]
    public void Entries_MapExpectedSources()
    {
        var sources = BackupCatalog.Entries.Select(e => e.SourceRelativePath).ToArray();

        Assert.Contains("settings.json", sources);
        Assert.Contains("themes", sources);
        Assert.Contains(Path.Combine("ssh", "profiles.json"), sources);
        Assert.Contains(Path.Combine("ssh", "native_known_hosts.json"), sources);
        Assert.Contains("workspaces", sources);
        Assert.Contains("workspace_templates", sources);
        Assert.Contains("policy", sources);
        Assert.Contains(Path.Combine("command-assist", "snippets.json"), sources);
    }

    [Fact]
    public void Entries_NeverIncludeExcludedTrees()
    {
        var sources = BackupCatalog.Entries.Select(e => e.SourceRelativePath).ToArray();

        Assert.DoesNotContain("logs", sources);
        Assert.DoesNotContain("recordings", sources);
        Assert.DoesNotContain("backups", sources);
        Assert.DoesNotContain("sessions", sources);
        Assert.DoesNotContain(Path.Combine("command-assist", "history.jsonl"), sources);
    }

    [Fact]
    public void BundlePaths_AreRelativeAndForwardSlashed()
    {
        foreach (var entry in BackupCatalog.Entries)
        {
            Assert.False(Path.IsPathRooted(entry.BundlePath));
            Assert.DoesNotContain('\\', entry.BundlePath);
        }
    }

    /// <summary>
    /// The other half of the drift guard's promise: paths that are NOT backed up or excluded
    /// must return false, and near-misses that share a textual prefix with a classified path
    /// (but are a different file/directory) must not be swept in by an overly loose prefix
    /// check. Without this, IsClassified could degenerate to "return true" and every other
    /// test in this file would still pass.
    /// </summary>
    [Fact]
    public void IsClassified_RejectsUnrelatedAndNearMissPaths()
    {
        // Positive boundary, for contrast.
        Assert.True(BackupCatalog.IsClassified(Path.Combine("themes", "nested", "deep.json")));
        Assert.True(BackupCatalog.IsClassified("ssh")); // parent of a backed-up file
        Assert.True(BackupCatalog.IsClassified(Path.Combine("logs", "debug.log"))); // excluded tree

        // Unrelated paths.
        Assert.False(BackupCatalog.IsClassified(Path.Combine("some", "unrelated", "path")));
        Assert.False(BackupCatalog.IsClassified("telemetry.json"));

        // Near-misses: share a textual prefix with a classified path but are a different
        // file/directory. A future "simplification" to a bare StartsWith(ancestor) must fail
        // these.
        Assert.False(BackupCatalog.IsClassified("settings.jsonx"));
        Assert.False(BackupCatalog.IsClassified("themes-backup"));

        // Same trick against an excluded path with a suffix, e.g. history.json vs history.jsonx.
        Assert.False(BackupCatalog.IsClassified(Path.Combine(CommandAssistRelativeDirectory, "history.jsonx")));
    }

    /// <summary>
    /// JsonlHistoryStore renames history.json to history.json.bak once migrated. It is not an
    /// AppPaths member, so the drift guard never sees it directly, but it is still
    /// privacy-sensitive command history that must never end up in a bundle.
    /// </summary>
    [Fact]
    public void HistoryJsonBak_IsExcluded()
    {
        Assert.True(BackupCatalog.IsClassified(Path.Combine(CommandAssistRelativeDirectory, "history.json.bak")));
        Assert.DoesNotContain(
            Path.Combine(CommandAssistRelativeDirectory, "history.json.bak"),
            BackupCatalog.Entries.Select(e => e.SourceRelativePath));
    }

    /// <summary>
    /// Drift guard: a new AppPaths member must be either backed up or explicitly excluded.
    /// Without this, a future path silently escapes backup and nobody notices until a
    /// restore comes back missing something.
    /// </summary>
    [Fact]
    public void EveryAppPathsMember_IsClassified()
    {
        string root = Path.GetFullPath(AppPaths.RootDirectory);

        var unclassified = new List<string>();
        foreach (var property in typeof(AppPaths).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (property.PropertyType != typeof(string)) continue;
            if (property.Name == nameof(AppPaths.RootDirectory)) continue;

            string value = Path.GetFullPath((string)property.GetValue(null)!);
            string relative = Path.GetRelativePath(root, value);

            if (!BackupCatalog.IsClassified(relative))
            {
                unclassified.Add($"{property.Name} -> {relative}");
            }
        }

        Assert.True(
            unclassified.Count == 0,
            "Unclassified AppPaths members. Add each to BackupCatalog.Entries or " +
            "BackupCatalog.ExcludedRelativePaths:\n  " + string.Join("\n  ", unclassified));
    }

    [Fact]
    public void BackupsDirectory_IsUnderRootAndExcluded()
    {
        string root = Path.GetFullPath(AppPaths.RootDirectory);
        string backups = Path.GetFullPath(AppPaths.BackupsDirectory);

        Assert.Equal(Path.Combine(root, "backups"), backups);
        Assert.True(BackupCatalog.IsClassified("backups"));
        Assert.DoesNotContain("backups", BackupCatalog.Entries.Select(e => e.SourceRelativePath));
    }
}
