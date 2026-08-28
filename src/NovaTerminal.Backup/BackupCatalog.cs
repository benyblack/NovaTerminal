using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaTerminal.Backup;

/// <summary>One backed-up path: where it lives in the app data tree, where it goes in the zip.</summary>
/// <param name="Category">Owning category.</param>
/// <param name="BundlePath">Path inside the zip. Always forward-slashed and relative.</param>
/// <param name="SourceRelativePath">Path relative to the app data root, in native separators.</param>
/// <param name="IsDirectory">True when the source is a directory copied recursively.</param>
public sealed record CatalogEntry(
    BackupCategory Category,
    string BundlePath,
    string SourceRelativePath,
    bool IsDirectory);

/// <summary>
/// The single mapping from <see cref="BackupCategory"/> to on-disk paths, plus the list of
/// app-data paths that are deliberately NOT backed up. Adding a new persisted path to the
/// app's data root means adding it here too — <c>BackupCatalogTests</c> fails otherwise.
/// </summary>
public static class BackupCatalog
{
    private static readonly string Ssh = "ssh";
    private static readonly string CommandAssist = "command-assist";

    public static IReadOnlyList<CatalogEntry> Entries { get; } = new CatalogEntry[]
    {
        new(BackupCategory.Settings, "settings/settings.json", "settings.json", false),
        new(BackupCategory.Themes, "themes", "themes", true),
        new(BackupCategory.Connections, "connections/profiles.json", Path.Combine(Ssh, "profiles.json"), false),
        new(BackupCategory.Connections, "connections/native_known_hosts.json", Path.Combine(Ssh, "native_known_hosts.json"), false),
        new(BackupCategory.Workspaces, "workspaces", "workspaces", true),
        new(BackupCategory.Workspaces, "workspace_templates", "workspace_templates", true),
        new(BackupCategory.Policy, "policy", "policy", true),
        new(BackupCategory.Snippets, "command-assist/snippets.json", Path.Combine(CommandAssist, "snippets.json"), false),
    };

    /// <summary>
    /// App-data paths deliberately left out of every bundle. Logs and recordings are large;
    /// history is the most privacy-sensitive file in the tree; last-session references
    /// machine-local working directories; backups must never nest inside backups.
    /// </summary>
    public static IReadOnlyList<string> ExcludedRelativePaths { get; } = new[]
    {
        "logs",
        "recordings",
        "sessions",
        "backups",
        "command-palette-usage.json",
        "vault.dat",
        Path.Combine(CommandAssist, "history.jsonl"),
        Path.Combine(CommandAssist, "history.json"),

        // JsonlHistoryStore renames history.json to history.json.bak once migrated to JSONL.
        // Same privacy-sensitive command history under a different name — never back it up.
        Path.Combine(CommandAssist, "history.json.bak"),

        // AtomicFile (used by TerminalSettings and CommandPaletteUsageStore) keeps a ".bak"
        // sibling of the previous good content next to every file it writes. Harmless if backed
        // up today, but inconsistent with the precedent set above for history.json.bak, and a
        // ".bak" is never something a restore should reintroduce as if it were current state
        // (M4).
        "command-palette-usage.json.bak",
        "settings.json.bak",
    };

    public static IReadOnlyList<BackupCategory> AllCategories { get; } =
        Enum.GetValues<BackupCategory>();

    public static IReadOnlyList<CatalogEntry> EntriesFor(BackupCategory category) =>
        Entries.Where(entry => entry.Category == category).ToArray();

    /// <summary>Absolute source path for an entry under <paramref name="root"/>.</summary>
    public static string ResolveSource(string root, CatalogEntry entry) =>
        Path.Combine(root, entry.SourceRelativePath);

    /// <summary>
    /// True when <paramref name="relativePath"/> (relative to the app data root) is accounted
    /// for: it is backed up, it is excluded, or it is a parent directory of something backed up
    /// (the <c>ssh</c> and <c>command-assist</c> directories, whose individual files are listed).
    /// </summary>
    public static bool IsClassified(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;

        string normalized = Normalize(relativePath);

        foreach (var entry in Entries)
        {
            string source = Normalize(entry.SourceRelativePath);
            if (IsSameOrUnder(normalized, source)) return true;
            if (IsSameOrUnder(source, normalized)) return true; // parent directory of a backed-up file
        }

        foreach (var excluded in ExcludedRelativePaths)
        {
            if (IsSameOrUnder(normalized, Normalize(excluded))) return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> (relative to the app data root) is itself, or
    /// falls under, one of the actual backed-up <see cref="Entries"/> — unlike
    /// <see cref="IsClassified"/>, this deliberately excludes <see cref="ExcludedRelativePaths"/>.
    /// <see cref="SnapshotScheduler"/> uses this as a positive allowlist (I2): only a change under
    /// a real backed-up path should ever wake the debounce. A denylist of "everything except a
    /// couple of known-noisy prefixes" previously let continuous writers like the debug log or
    /// command-assist history keep the debounce from ever elapsing, since both live under the
    /// watched tree and matched nothing on the denylist.
    /// </summary>
    public static bool IsBackedUpPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        string normalized = Normalize(relativePath);

        foreach (var entry in Entries)
        {
            if (IsSameOrUnder(normalized, Normalize(entry.SourceRelativePath))) return true;
        }

        return false;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static bool IsSameOrUnder(string candidate, string ancestor)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, ancestor, comparison)
            || candidate.StartsWith(ancestor + "/", comparison);
    }
}
