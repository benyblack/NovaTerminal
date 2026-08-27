using System.Reflection;
using NovaTerminal.Shell;
using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupCatalogTests
{
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
