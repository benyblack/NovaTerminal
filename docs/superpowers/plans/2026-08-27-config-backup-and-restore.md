# Configuration Backup and Restore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user export NovaTerminal's configuration to one portable `.novabackup` file, import it back with merge or replace semantics, and roll back to an automatic local snapshot.

**Architecture:** One module, `src/NovaTerminal.App/Shell/Backup/`, containing a `BackupCatalog` (the single map from category to on-disk paths), a zip reader/writer pair, and a `BackupService` that every surface calls. The service takes a root directory in its constructor rather than reading `AppPaths` statically, so all of it is testable against a temp tree. Four thin surfaces — Settings window, command palette, CLI, MCP — wrap the same service.

**Tech Stack:** C# / .NET 10, Avalonia (UI), `System.IO.Compression.ZipArchive` (in-box, no new package), `System.Text.Json` source-generated contexts, xunit.v3 + Moq for tests.

**Spec:** `docs/superpowers/specs/2026-08-27-config-backup-and-restore-design.md`

## Global Constraints

- Target framework is `net10.0`, `Nullable` is `enable`, `LangVersion` is `latest`. All new code must be null-annotation clean.
- **Never use raw `dotnet build` / `dotnet test`.** Always `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>` (bash). Raw invocations spawn MSBuild daemons that inherit stdout and hang the caller.
- Run tests targeted, never solution-wide — a full `dotnet test` is 20–30 minutes. Use `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "..."`.
- **No secret material in a bundle, ever.** No reads from `ISecretStore`/`VaultService` anywhere in `Shell/Backup/`. This is the position from issue #100.
- Bundle manifest JSON is **camelCase**. `settings.json` on disk is **PascalCase** and must stay that way — the import path must not re-case it.
- `NovaTerminal.App.Tests` runs on ubuntu in CI as well as Windows. Tests must be POSIX-portable: no `FileShare.None` lock tricks (a lock blocks reads but not rename/delete on POSIX), no backslash path literals — use `Path.Combine`.
- No new test project. Everything lands in `tests/NovaTerminal.App.Tests`, so `.github/workflows/ci.yml` needs no changes.
- File writes to live config go through the existing `AtomicFile` helper (`src/NovaTerminal.App/Shell/AtomicFile.cs`, `internal static`).
- Commit after every task with a `feat:`/`test:`/`docs:` prefix. Do not push unless asked.

## File Structure

**Create — `src/NovaTerminal.App/Shell/Backup/`:**

| File | Responsibility |
| --- | --- |
| `BackupCategory.cs` | The `BackupCategory` enum and `SnapshotReason`, `ImportMode` enums. |
| `BackupCatalog.cs` | Category → path entries, the exclusion list, and `IsClassified`. The one place that knows what is backed up. |
| `BackupManifest.cs` | The manifest record and `BackupJsonContext` source-gen context. |
| `BackupResults.cs` | `BackupFailureKind`, `BackupOutcome`, `InspectOutcome`, `BundleInspection`, `SnapshotInfo`. |
| `BundleWriter.cs` | Writes a zip from a root directory + category set. |
| `BundleReader.cs` | Opens a zip, validates the manifest, exposes entries. |
| `BackupService.cs` | `Export` / `Inspect` / `Import` / `Snapshot` / `ListSnapshots` / `Restore`. |
| `SnapshotScheduler.cs` | `FileSystemWatcher` + debounce timer → `Snapshot(SnapshotReason.Auto)`. |
| `BackupCommand.cs` | CLI verb parsing and execution (lives here, not in `Shell/`, to keep the module together). |

**Modify:**

| File | Change |
| --- | --- |
| `src/NovaTerminal.App/Shell/AppPaths.cs` | Add `BackupsDirectory`; create it in `EnsureInitialized`. |
| `src/NovaTerminal.Cli/Program.cs` | Add the `BackupCommand` branch to the dispatch chain. |
| `src/NovaTerminal.App/SettingsWindow.axaml` | New `DataNav` sidebar group + a new `TabItem` at the END of the `TabControl`. |
| `src/NovaTerminal.App/SettingsWindow.axaml.cs` | Wire the new nav group and extend `SyncSidebarFromTabs`; wire the buttons. |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | Register three palette commands in `SetupCommandPalette()`. |
| `src/NovaTerminal.McpServer/Tools/BackupTools.cs` | New MCP tool type (create). |

**Test files — all under `tests/NovaTerminal.App.Tests/Backup/`:** `BackupCatalogTests.cs`, `BundleRoundTripTests.cs`, `BackupExportTests.cs`, `BackupImportTests.cs`, `SnapshotTests.cs`, `SnapshotSchedulerTests.cs`, `BackupCommandTests.cs`. Plus `tests/NovaTerminal.McpServer.Tests/BackupToolsTests.cs`.

**Shared test helper** (created in Task 1, used by every later task): `tests/NovaTerminal.App.Tests/Backup/BackupTestTree.cs`.

---

### Task 1: Catalog, paths, and the drift guard

Establishes the single source of truth for what gets backed up, and the test that stops a future `AppPaths` member from silently escaping.

**Files:**
- Create: `src/NovaTerminal.App/Shell/Backup/BackupCategory.cs`
- Create: `src/NovaTerminal.App/Shell/Backup/BackupCatalog.cs`
- Modify: `src/NovaTerminal.App/Shell/AppPaths.cs` (add `BackupsDirectory`, create it in `EnsureInitialized`)
- Create: `tests/NovaTerminal.App.Tests/Backup/BackupTestTree.cs`
- Test: `tests/NovaTerminal.App.Tests/Backup/BackupCatalogTests.cs`

**Interfaces:**
- Consumes: `NovaTerminal.Shell.AppPaths` (existing).
- Produces: `BackupCategory`, `SnapshotReason`, `ImportMode` enums; `CatalogEntry` record; `BackupCatalog.Entries`, `BackupCatalog.EntriesFor(BackupCategory)`, `BackupCatalog.ExcludedRelativePaths`, `BackupCatalog.IsClassified(string relativePath)`, `BackupCatalog.AllCategories`; `AppPaths.BackupsDirectory`; test helper `BackupTestTree`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/BackupCatalogTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupCatalogTests"
```

Expected: compile failure — `BackupCatalog` and `AppPaths.BackupsDirectory` do not exist.

- [ ] **Step 3: Add `BackupsDirectory` to AppPaths**

In `src/NovaTerminal.App/Shell/AppPaths.cs`, add next to the other directory properties (after `RecordingsDirectory`):

```csharp
        /// <summary>Automatic configuration snapshots written by <c>BackupService</c>.</summary>
        public static string BackupsDirectory => Path.Combine(RootDirectory, "backups");
```

and inside `EnsureInitialized`'s `try` block, next to the other `Directory.CreateDirectory` calls:

```csharp
                    Directory.CreateDirectory(BackupsDirectory);
```

- [ ] **Step 4: Create the enums**

Create `src/NovaTerminal.App/Shell/Backup/BackupCategory.cs`:

```csharp
namespace NovaTerminal.Shell.Backup;

/// <summary>
/// A unit of configuration a bundle can carry. The manifest stores these as
/// lowercase names; see <see cref="BackupCatalog"/> for the path mapping.
/// </summary>
public enum BackupCategory
{
    Settings,
    Themes,
    Connections,
    Workspaces,
    Policy,
    Snippets
}

/// <summary>Why a snapshot was written. Encoded as the snapshot file-name prefix.</summary>
public enum SnapshotReason
{
    /// <summary>Written by <c>SnapshotScheduler</c> after tracked files changed.</summary>
    Auto,

    /// <summary>Forced immediately before an import.</summary>
    PreImport,

    /// <summary>Forced immediately before a restore.</summary>
    PreRestore
}

/// <summary>How an import reconciles bundle content with what is already on disk.</summary>
public enum ImportMode
{
    /// <summary>Bundle wins per item; local items with no counterpart survive.</summary>
    Merge,

    /// <summary>For each included category the bundle becomes the truth.</summary>
    Replace
}
```

- [ ] **Step 5: Create the catalog**

Create `src/NovaTerminal.App/Shell/Backup/BackupCatalog.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaTerminal.Shell.Backup;

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
/// app-data paths that are deliberately NOT backed up. Adding a new persisted path to
/// <see cref="AppPaths"/> means adding it here too — <c>BackupCatalogTests</c> fails otherwise.
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
```

- [ ] **Step 6: Create the shared test helper**

Create `tests/NovaTerminal.App.Tests/Backup/BackupTestTree.cs`. Every later task uses this — it builds a populated fake app-data root and disposes it.

```csharp
using System.Text.Json;

namespace NovaTerminal.Tests.Backup;

/// <summary>
/// A disposable temp app-data root pre-populated with realistic content, so backup tests
/// never touch the real profile. Not tied to NOVATERM_APPDATA_ROOT — BackupService takes a
/// root explicitly, which keeps these tests parallel-safe.
/// </summary>
public sealed class BackupTestTree : IDisposable
{
    public string Root { get; }

    private BackupTestTree(string root) => Root = root;

    public static BackupTestTree CreatePopulated()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nova_backup_test_{Guid.NewGuid():N}");
        var tree = new BackupTestTree(root);

        tree.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Default"}""");
        tree.WriteFile(Path.Combine("themes", "solarized.json"), """{"name":"Solarized"}""");
        tree.WriteFile(Path.Combine("ssh", "profiles.json"), """{"schemaVersion":1,"profiles":[]}""");
        tree.WriteFile(Path.Combine("ssh", "native_known_hosts.json"), "[]");
        tree.WriteFile(Path.Combine("workspaces", "default.json"), """{"name":"default"}""");
        tree.WriteFile(Path.Combine("workspace_templates", "dev.json"), """{"name":"dev"}""");
        tree.WriteFile(Path.Combine("policy", "workspace_policy.json"), "{}");
        tree.WriteFile(Path.Combine("command-assist", "snippets.json"), "[]");

        // Excluded content — must never appear in a bundle.
        tree.WriteFile(Path.Combine("logs", "debug.log"), "log line");
        tree.WriteFile(Path.Combine("recordings", "session.cast"), "recording");
        tree.WriteFile(Path.Combine("sessions", "last_session.json"), "{}");
        tree.WriteFile(Path.Combine("command-assist", "history.jsonl"), """{"cmd":"secret-history-entry"}""");
        tree.WriteFile("command-palette-usage.json", "{}");

        return tree;
    }

    /// <summary>An empty root, for import-into-fresh-machine tests.</summary>
    public static BackupTestTree CreateEmpty()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nova_backup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new BackupTestTree(root);
    }

    public void WriteFile(string relativePath, string contents)
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(Root, relativePath));

    public JsonDocument ReadJson(string relativePath) => JsonDocument.Parse(ReadFile(relativePath));

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* temp cleanup is best-effort */ }
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupCatalogTests"
```

Expected: PASS, 6 tests. If `EveryAppPathsMember_IsClassified` fails, its message names the offending members — add each to `Entries` or `ExcludedRelativePaths`.

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup tests/NovaTerminal.App.Tests/Backup src/NovaTerminal.App/Shell/AppPaths.cs
git commit -m "feat(backup): catalog of backed-up paths with AppPaths drift guard"
```

---

### Task 2: Manifest, results, and the zip round trip

**Files:**
- Create: `src/NovaTerminal.App/Shell/Backup/BackupManifest.cs`
- Create: `src/NovaTerminal.App/Shell/Backup/BackupResults.cs`
- Create: `src/NovaTerminal.App/Shell/Backup/BundleWriter.cs`
- Create: `src/NovaTerminal.App/Shell/Backup/BundleReader.cs`
- Test: `tests/NovaTerminal.App.Tests/Backup/BundleRoundTripTests.cs`

**Interfaces:**
- Consumes: `BackupCatalog`, `CatalogEntry`, `BackupCategory` from Task 1; `BackupTestTree` in tests.
- Produces: `BackupManifest` record; `BackupManifest.CurrentSchemaVersion`; `BackupFailureKind` enum; `BackupOutcome` and `InspectOutcome` records; `BundleInspection` record; `SnapshotInfo` record; `BundleWriter.Write(string root, string destinationPath, IReadOnlyCollection<BackupCategory> categories, BackupManifest manifest)`; `BundleReader.Open(string bundlePath)` returning `InspectOutcome`; `BundleReader.ExtractTo(string bundlePath, string destinationRoot, IReadOnlyCollection<BackupCategory> categories)`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/BundleRoundTripTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using NovaTerminal.Shell.Backup;

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
        Assert.Equal(1, outcome.Inspection.ItemCounts[BackupCategory.Themes]);
        Assert.Equal(2, outcome.Inspection.ItemCounts[BackupCategory.Connections]);
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
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BundleRoundTripTests"
```

Expected: compile failure — `BundleWriter`, `BundleReader`, `BackupManifest`, `BackupFailureKind` do not exist.

- [ ] **Step 3: Create the manifest**

Create `src/NovaTerminal.App/Shell/Backup/BackupManifest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaTerminal.Shell.Backup;

/// <summary>
/// The <c>manifest.json</c> at the root of every bundle. camelCase on the wire — note this
/// differs from settings.json, which is PascalCase on disk and must stay that way.
/// </summary>
public sealed record BackupManifest
{
    /// <summary>Bundle format version. Bump when the on-disk layout changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string AppVersion { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public string Machine { get; init; } = string.Empty;
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
internal partial class BackupJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 4: Create the result types**

Create `src/NovaTerminal.App/Shell/Backup/BackupResults.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace NovaTerminal.Shell.Backup;

/// <summary>Why a backup operation failed. <see cref="None"/> means it succeeded.</summary>
public enum BackupFailureKind
{
    None,
    NotFound,
    NotABackup,
    CorruptArchive,
    UnsupportedSchemaVersion,
    MissingCategoryContent,
    WriteFailed
}

/// <summary>Result of an operation with no return value.</summary>
public sealed record BackupOutcome(bool Success, BackupFailureKind Failure, string Message)
{
    public static BackupOutcome Ok(string message = "") =>
        new(true, BackupFailureKind.None, message);

    public static BackupOutcome Fail(BackupFailureKind kind, string message) =>
        new(false, kind, message);
}

/// <summary>What a bundle contains, without extracting it.</summary>
public sealed record BundleInspection(
    BackupManifest Manifest,
    IReadOnlyDictionary<BackupCategory, int> ItemCounts);

/// <summary>Result of reading a bundle's manifest.</summary>
public sealed record InspectOutcome(
    bool Success,
    BackupFailureKind Failure,
    string Message,
    BundleInspection? Inspection)
{
    public static InspectOutcome Ok(BundleInspection inspection) =>
        new(true, BackupFailureKind.None, string.Empty, inspection);

    public static InspectOutcome Fail(BackupFailureKind kind, string message) =>
        new(false, kind, message, null);
}

/// <summary>A snapshot on disk. <paramref name="Id"/> is the file-name stem.</summary>
public sealed record SnapshotInfo(
    string Id,
    SnapshotReason Reason,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string ContentHash,
    string FilePath);
```

- [ ] **Step 5: Create the writer**

Create `src/NovaTerminal.App/Shell/Backup/BundleWriter.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NovaTerminal.Shell.Backup;

/// <summary>Writes a <c>.novabackup</c> zip from an app-data root.</summary>
public static class BundleWriter
{
    public static void Write(
        string root,
        string destinationPath,
        IReadOnlyCollection<BackupCategory> categories,
        BackupManifest manifest)
    {
        string? destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        // Write to a temp sibling and move, so an interrupted export never leaves a
        // half-written file that later reads as a corrupt bundle.
        string temp = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteManifest(zip, manifest);

                foreach (var entry in BackupCatalog.Entries.Where(e => categories.Contains(e.Category)))
                {
                    string source = BackupCatalog.ResolveSource(root, entry);
                    if (entry.IsDirectory)
                    {
                        WriteDirectory(zip, source, entry.BundlePath);
                    }
                    else if (File.Exists(source))
                    {
                        WriteFile(zip, source, entry.BundlePath);
                    }
                }
            }

            File.Move(temp, destinationPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static void WriteManifest(ZipArchive zip, BackupManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest);
        var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(json));
    }

    private static void WriteDirectory(ZipArchive zip, string sourceDirectory, string bundlePrefix)
    {
        if (!Directory.Exists(sourceDirectory)) return;

        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            WriteFile(zip, file, $"{bundlePrefix}/{relative}");
        }
    }

    private static void WriteFile(ZipArchive zip, string sourceFile, string bundlePath)
    {
        var entry = zip.CreateEntry(bundlePath, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var fileStream = File.OpenRead(sourceFile);
        fileStream.CopyTo(entryStream);
    }
}
```

- [ ] **Step 6: Create the reader**

Create `src/NovaTerminal.App/Shell/Backup/BundleReader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace NovaTerminal.Shell.Backup;

/// <summary>Reads and validates a <c>.novabackup</c> zip.</summary>
public static class BundleReader
{
    /// <summary>Reads the manifest and counts entries per category. Extracts nothing.</summary>
    public static InspectOutcome Open(string bundlePath)
    {
        if (!File.Exists(bundlePath))
        {
            return InspectOutcome.Fail(BackupFailureKind.NotFound, $"No such file: {bundlePath}");
        }

        try
        {
            using var zip = ZipFile.OpenRead(bundlePath);

            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                return InspectOutcome.Fail(
                    BackupFailureKind.NotABackup,
                    $"{Path.GetFileName(bundlePath)} has no manifest.json — not a NovaTerminal backup.");
            }

            BackupManifest? manifest;
            try
            {
                using var manifestStream = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize(manifestStream, BackupJsonContext.Default.BackupManifest);
            }
            catch (JsonException ex)
            {
                return InspectOutcome.Fail(
                    BackupFailureKind.NotABackup,
                    $"{Path.GetFileName(bundlePath)} has an unreadable manifest: {ex.Message}");
            }

            if (manifest is null)
            {
                return InspectOutcome.Fail(
                    BackupFailureKind.NotABackup,
                    $"{Path.GetFileName(bundlePath)} has an empty manifest.");
            }

            if (manifest.SchemaVersion > BackupManifest.CurrentSchemaVersion)
            {
                return InspectOutcome.Fail(
                    BackupFailureKind.UnsupportedSchemaVersion,
                    $"Bundle uses schema version {manifest.SchemaVersion}; this build understands up to " +
                    $"{BackupManifest.CurrentSchemaVersion}. Update NovaTerminal to import it.");
            }

            // Schema v1 is the first version, so there are no migrations yet. When v2 lands,
            // run registered migrations here for manifest.SchemaVersion < CurrentSchemaVersion.

            var counts = CountItems(zip);

            foreach (string name in manifest.Categories)
            {
                if (!Enum.TryParse<BackupCategory>(name, ignoreCase: true, out var category)) continue;
                if (!counts.TryGetValue(category, out int count) || count == 0)
                {
                    return InspectOutcome.Fail(
                        BackupFailureKind.MissingCategoryContent,
                        $"Bundle claims category '{name}' but contains no files for it — the archive is corrupt.");
                }
            }

            return InspectOutcome.Ok(new BundleInspection(manifest, counts));
        }
        catch (InvalidDataException ex)
        {
            return InspectOutcome.Fail(
                BackupFailureKind.CorruptArchive,
                $"{Path.GetFileName(bundlePath)} is not a readable archive: {ex.Message}");
        }
        catch (IOException ex)
        {
            return InspectOutcome.Fail(
                BackupFailureKind.CorruptArchive,
                $"Could not read {Path.GetFileName(bundlePath)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the requested categories into <paramref name="destinationRoot"/>, laid out as an
    /// app-data tree. Callers extract into a staging directory, never over live config.
    /// </summary>
    public static void ExtractTo(
        string bundlePath,
        string destinationRoot,
        IReadOnlyCollection<BackupCategory> categories)
    {
        using var zip = ZipFile.OpenRead(bundlePath);

        foreach (var catalogEntry in BackupCatalog.Entries.Where(e => categories.Contains(e.Category)))
        {
            foreach (var zipEntry in EntriesUnder(zip, catalogEntry))
            {
                string relativeInsideEntry = catalogEntry.IsDirectory
                    ? zipEntry.FullName[(catalogEntry.BundlePath.Length + 1)..]
                    : string.Empty;

                string destination = catalogEntry.IsDirectory
                    ? Path.Combine(destinationRoot, catalogEntry.SourceRelativePath, relativeInsideEntry.Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(destinationRoot, catalogEntry.SourceRelativePath);

                // Zip-slip guard: a hand-edited archive must not write outside the destination.
                // Compare via GetRelativePath, NOT a StartsWith prefix test — a bare prefix
                // check accepts a same-prefix sibling (root "/tmp/x" would admit
                // "/tmp/xevil/payload") because there is no path-boundary anchor.
                string fullDestination = Path.GetFullPath(destination);
                string fullRoot = Path.GetFullPath(destinationRoot);
                string relativeToRoot = Path.GetRelativePath(fullRoot, fullDestination);
                if (Path.IsPathRooted(relativeToRoot)
                    || relativeToRoot == ".."
                    || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Bundle entry '{zipEntry.FullName}' escapes the destination tree.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
                zipEntry.ExtractToFile(fullDestination, overwrite: true);
            }
        }
    }

    /// <summary>Zip entries belonging to one catalog entry: an exact match, or everything under a prefix.</summary>
    public static IEnumerable<ZipArchiveEntry> EntriesUnder(ZipArchive zip, CatalogEntry catalogEntry)
    {
        if (!catalogEntry.IsDirectory)
        {
            var exact = zip.GetEntry(catalogEntry.BundlePath);
            if (exact is not null) yield return exact;
            yield break;
        }

        string prefix = catalogEntry.BundlePath + "/";
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.Length >= 0
                && !entry.FullName.EndsWith('/'))
            {
                yield return entry;
            }
        }
    }

    private static Dictionary<BackupCategory, int> CountItems(ZipArchive zip)
    {
        var counts = new Dictionary<BackupCategory, int>();
        foreach (var category in BackupCatalog.AllCategories)
        {
            int total = 0;
            foreach (var catalogEntry in BackupCatalog.EntriesFor(category))
            {
                total += EntriesUnder(zip, catalogEntry).Count();
            }

            counts[category] = total;
        }

        return counts;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BundleRoundTripTests"
```

Expected: PASS, 12 tests.

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup tests/NovaTerminal.App.Tests/Backup
git commit -m "feat(backup): bundle manifest, writer, reader with validation"
```

---

### Task 3: BackupService.Export and Inspect

**Files:**
- Create: `src/NovaTerminal.App/Shell/Backup/BackupService.cs`
- Test: `tests/NovaTerminal.App.Tests/Backup/BackupExportTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–2.
- Produces: `BackupService(string rootDirectory, TimeProvider? timeProvider = null)`; `BackupService.RootDirectory`; `BackupOutcome Export(string destinationPath, IReadOnlyCollection<BackupCategory>? categories = null)`; `InspectOutcome Inspect(string bundlePath)`; `const string BundleExtension = ".novabackup"`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/BackupExportTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using NovaTerminal.Shell.Backup;

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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupExportTests"
```

Expected: compile failure — `BackupService` does not exist.

- [ ] **Step 3: Create BackupService with Export and Inspect**

Create `src/NovaTerminal.App/Shell/Backup/BackupService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NovaTerminal.Shell.Backup;

/// <summary>
/// Export, import, snapshot, and restore of NovaTerminal configuration.
///
/// Takes its app-data root as a constructor argument rather than reading <see cref="AppPaths"/>
/// statically, so tests drive it against a temp tree without touching the real profile.
///
/// Never reads secret storage. A bundle carries connection profiles with their
/// <c>RememberPasswordInVault</c> flag but no password material (issue #100).
/// </summary>
public sealed class BackupService
{
    public const string BundleExtension = ".novabackup";

    private readonly TimeProvider _timeProvider;

    public BackupService(string rootDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RootDirectory { get; }

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    /// <summary>Writes a bundle at <paramref name="destinationPath"/>.</summary>
    /// <param name="categories">Null means every category.</param>
    public BackupOutcome Export(string destinationPath, IReadOnlyCollection<BackupCategory>? categories = null)
    {
        var requested = categories ?? BackupCatalog.AllCategories;
        var present = requested.Where(HasContent).ToArray();

        var manifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,
            AppVersion = ResolveAppVersion(),
            CreatedUtc = _timeProvider.GetUtcNow(),
            Machine = SafeMachineName(),
            Categories = present.Select(c => c.ToString().ToLowerInvariant()).ToArray()
        };

        try
        {
            BundleWriter.Write(RootDirectory, destinationPath, present, manifest);
            return BackupOutcome.Ok($"Exported {present.Length} categories to {destinationPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                $"Could not write {destinationPath}: {ex.Message}");
        }
    }

    /// <summary>Reads a bundle's manifest and item counts without extracting anything.</summary>
    public InspectOutcome Inspect(string bundlePath) => BundleReader.Open(bundlePath);

    /// <summary>True when at least one file exists on disk for this category.</summary>
    private bool HasContent(BackupCategory category)
    {
        foreach (var entry in BackupCatalog.EntriesFor(category))
        {
            string source = BackupCatalog.ResolveSource(RootDirectory, entry);
            if (entry.IsDirectory)
            {
                if (Directory.Exists(source) && Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any())
                {
                    return true;
                }
            }
            else if (File.Exists(source))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveAppVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return "unknown"; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupExportTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup/BackupService.cs tests/NovaTerminal.App.Tests/Backup/BackupExportTests.cs
git commit -m "feat(backup): BackupService export and inspect"
```

---

### Task 4: Snapshots — write, list, dedupe, retention

Snapshots come before import so that `Import` can call a real `Snapshot`, and `Restore` (Task 5) can call a real `Import`. No stubs, no circular dependency.

**Files:**
- Modify: `src/NovaTerminal.App/Shell/Backup/BackupService.cs` (add `Snapshot`, `ListSnapshots`, retention, hashing)
- Test: `tests/NovaTerminal.App.Tests/Backup/SnapshotTests.cs`

**Interfaces:**
- Consumes: `BackupService` from Task 3, `SnapshotInfo` and `SnapshotReason` from Tasks 1–2.
- Produces: `SnapshotInfo? Snapshot(SnapshotReason reason)`; `IReadOnlyList<SnapshotInfo> ListSnapshots()`; `const int MaxSnapshots = 20`; `static readonly TimeSpan SnapshotRetentionWindow = TimeSpan.FromDays(7)`. Task 5 adds `Restore`, which builds on both.

**Snapshot id format:** `<reason>-<yyyyMMddTHHmmssZ>-<hash8>`, e.g. `auto-20260827T091400Z-a1b2c3d4`. Reason encodes as `auto`, `pre-import`, `pre-restore`.

**Content hash:** SHA-256 over the ordered concatenation of each backed-up file's bundle path and bytes — computed from the live tree, not the zip, because zip bytes vary with entry timestamps. First 8 hex chars go in the id.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/SnapshotTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~SnapshotTests"
```

Expected: compile failure — `Snapshot`, `ListSnapshots`, `MaxSnapshots` do not exist.

- [ ] **Step 3: Add snapshot support to BackupService**

In `src/NovaTerminal.App/Shell/Backup/BackupService.cs`, add `using System.Globalization;` and `using System.Security.Cryptography;` at the top, then add these members after `Inspect`:

```csharp
    /// <summary>Snapshots kept regardless of age.</summary>
    public const int MaxSnapshots = 20;

    /// <summary>Snapshots newer than this are kept regardless of count.</summary>
    public static readonly TimeSpan SnapshotRetentionWindow = TimeSpan.FromDays(7);

    private const string SnapshotTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>
    /// Writes a snapshot of the current configuration into <see cref="BackupsDirectory"/>.
    /// Returns null when an <see cref="SnapshotReason.Auto"/> snapshot was skipped because the
    /// content is byte-identical to the newest existing snapshot. Forced reasons always write.
    /// </summary>
    /// <remarks>
    /// Never throws. A failing backup must not block a settings save, so failures are logged
    /// and reported as a null return.
    /// </remarks>
    public SnapshotInfo? Snapshot(SnapshotReason reason)
    {
        try
        {
            string hash = ComputeContentHash();

            if (reason == SnapshotReason.Auto)
            {
                var newest = ListSnapshots().FirstOrDefault();
                if (newest is not null && string.Equals(newest.ContentHash, hash, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            var now = _timeProvider.GetUtcNow();
            string id = $"{ReasonToken(reason)}-{now.UtcDateTime.ToString(SnapshotTimestampFormat, CultureInfo.InvariantCulture)}-{hash[..8]}";
            string path = Path.Combine(BackupsDirectory, id + BundleExtension);

            Directory.CreateDirectory(BackupsDirectory);

            var outcome = Export(path);
            if (!outcome.Success)
            {
                AppLogger.Log($"[backup] snapshot failed: {outcome.Message}");
                return null;
            }

            PruneSnapshots();

            return new SnapshotInfo(id, reason, now, new FileInfo(path).Length, hash, path);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[backup] snapshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Snapshots on disk, newest first. Unparseable file names are ignored.</summary>
    public IReadOnlyList<SnapshotInfo> ListSnapshots()
    {
        if (!Directory.Exists(BackupsDirectory)) return Array.Empty<SnapshotInfo>();

        var results = new List<SnapshotInfo>();
        foreach (string path in Directory.GetFiles(BackupsDirectory, "*" + BundleExtension))
        {
            if (TryParseSnapshot(path, out var info)) results.Add(info!);
        }

        return results
            .OrderByDescending(s => s.CreatedUtc)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// SHA-256 over every backed-up file's bundle path and bytes, in catalog order. Computed
    /// from the live tree rather than the zip: zip bytes carry entry timestamps, so two
    /// archives of identical content do not compare equal.
    /// </summary>
    private string ComputeContentHash()
    {
        using var sha = SHA256.Create();
        using var buffer = new MemoryStream();

        foreach (var entry in BackupCatalog.Entries)
        {
            string source = BackupCatalog.ResolveSource(RootDirectory, entry);

            if (entry.IsDirectory)
            {
                if (!Directory.Exists(source)) continue;
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.Ordinal))
                {
                    string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                    AppendToHash(buffer, $"{entry.BundlePath}/{relative}", File.ReadAllBytes(file));
                }
            }
            else if (File.Exists(source))
            {
                AppendToHash(buffer, entry.BundlePath, File.ReadAllBytes(source));
            }
        }

        buffer.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(buffer)).ToLowerInvariant();
    }

    private static void AppendToHash(Stream target, string path, byte[] content)
    {
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path + "\n");
        target.Write(pathBytes);
        target.Write(content);
    }

    /// <summary>Keeps the union of the newest <see cref="MaxSnapshots"/> and everything inside the retention window.</summary>
    private void PruneSnapshots()
    {
        var snapshots = ListSnapshots();
        if (snapshots.Count <= MaxSnapshots) return;

        var cutoff = _timeProvider.GetUtcNow() - SnapshotRetentionWindow;

        var keep = new HashSet<string>(
            snapshots.Take(MaxSnapshots).Select(s => s.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshots.Where(s => s.CreatedUtc >= cutoff))
        {
            keep.Add(snapshot.Id);
        }

        foreach (var snapshot in snapshots.Where(s => !keep.Contains(s.Id)))
        {
            try { File.Delete(snapshot.FilePath); }
            catch (Exception ex) { AppLogger.Log($"[backup] could not prune {snapshot.Id}: {ex.Message}"); }
        }
    }

    private static string ReasonToken(SnapshotReason reason) => reason switch
    {
        SnapshotReason.Auto => "auto",
        SnapshotReason.PreImport => "pre-import",
        SnapshotReason.PreRestore => "pre-restore",
        _ => "auto"
    };

    private static bool TryParseReason(string token, out SnapshotReason reason)
    {
        switch (token)
        {
            case "auto": reason = SnapshotReason.Auto; return true;
            case "pre-import": reason = SnapshotReason.PreImport; return true;
            case "pre-restore": reason = SnapshotReason.PreRestore; return true;
            default: reason = SnapshotReason.Auto; return false;
        }
    }

    /// <summary>Parses <c>&lt;reason&gt;-&lt;timestamp&gt;-&lt;hash8&gt;</c>. The reason itself may contain a dash.</summary>
    private static bool TryParseSnapshot(string path, out SnapshotInfo? info)
    {
        info = null;
        string id = Path.GetFileNameWithoutExtension(path);

        int hashSeparator = id.LastIndexOf('-');
        if (hashSeparator <= 0) return false;

        int timestampSeparator = id.LastIndexOf('-', hashSeparator - 1);
        if (timestampSeparator <= 0) return false;

        string reasonToken = id[..timestampSeparator];
        string timestampToken = id[(timestampSeparator + 1)..hashSeparator];
        string hashToken = id[(hashSeparator + 1)..];

        if (!TryParseReason(reasonToken, out var reason)) return false;

        if (!DateTimeOffset.TryParseExact(
                timestampToken,
                SnapshotTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var created))
        {
            return false;
        }

        info = new SnapshotInfo(id, reason, created, new FileInfo(path).Length, hashToken, path);
        return true;
    }
```

`AppLogger.Log(string)` is `public static` in `NovaTerminal.Shell` (`src/NovaTerminal.App/Shell/AppLogger.cs`), the parent namespace, so it resolves without an extra `using`.

Two consequences of the hash living in the id: `ListSnapshots` reads `ContentHash` straight from the file name, so no zip is opened to list, and the dedupe check in `Snapshot` compares against the newest snapshot's parsed hash.

- [ ] **Step 4: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~SnapshotTests"
```

Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup/BackupService.cs tests/NovaTerminal.App.Tests/Backup/SnapshotTests.cs
git commit -m "feat(backup): snapshots with hash dedupe and retention"
```

---

### Task 5: Import with merge and replace, plus Restore

The riskiest task — it writes over live config. Staging plus a forced pre-import snapshot is what makes it safe. `Snapshot` already exists from Task 4, so `Import` calls the real thing and `Restore` is a thin wrapper over `Import`.

**Files:**
- Modify: `src/NovaTerminal.App/Shell/Backup/BackupService.cs` (add `Import`, `Restore`, and their private helpers)
- Test: `tests/NovaTerminal.App.Tests/Backup/BackupImportTests.cs`

**Interfaces:**
- Consumes: `BackupService` with `Export`/`Inspect`/`Snapshot`/`ListSnapshots` from Tasks 3–4, `BundleReader.ExtractTo` from Task 2, `ImportMode` from Task 1.
- Produces: `BackupOutcome Import(string bundlePath, ImportMode mode, IReadOnlyCollection<BackupCategory>? categories = null)`; `BackupOutcome Restore(string snapshotId)`.

**Semantics to implement exactly:**

| Category | Merge | Replace |
| --- | --- | --- |
| Settings | Key-by-key over the raw `JsonObject`; bundle wins per key; local-only keys survive. Casing preserved. | Bundle file replaces local file wholesale. |
| Themes / Workspaces / Policy (directories) | Per-file: bundle file overwrites same-named local file; local-only files survive. | Local directory is emptied, then bundle files written. |
| Connections (`profiles.json`) | Merge the `profiles` array by `Id`; bundle entry replaces the local one with the same `Id`; local-only ids survive. `native_known_hosts.json` merges by whole-array union, deduped by serialized entry. | Bundle files replace local files wholesale. |
| Snippets | Bundle file replaces local file wholesale (merge and replace are the same — the file is a flat array with no stable id). | Same. |

**Restore** is always a Replace of the categories the snapshot contains — a rollback, not a merge — and takes a `pre-restore` snapshot first.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/BackupImportTests.cs`:

```csharp
using System.Text.Json;
using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupImportTests
{
    [Fact]
    public void Import_IntoEmptyTree_ReproducesSource()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreateEmpty();
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(source.ReadFile("settings.json"), target.ReadFile("settings.json"));
        Assert.Equal(
            source.ReadFile(Path.Combine("themes", "solarized.json")),
            target.ReadFile(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Merge_Settings_BundleWinsPerKey_LocalOnlyKeysSurvive()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":20,"ThemeName":"Solarized"}""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"CursorBlink":false}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        using var merged = target.ReadJson("settings.json");
        Assert.Equal(20, merged.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("Solarized", merged.RootElement.GetProperty("ThemeName").GetString());
        Assert.False(merged.RootElement.GetProperty("CursorBlink").GetBoolean());
    }

    [Fact]
    public void Replace_Settings_DropsLocalOnlyKeys()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":20}""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"CursorBlink":false}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        using var replaced = target.ReadJson("settings.json");
        Assert.Equal(20, replaced.RootElement.GetProperty("FontSize").GetInt32());
        Assert.False(replaced.RootElement.TryGetProperty("CursorBlink", out _));
    }

    [Fact]
    public void Merge_Themes_KeepsLocalOnlyTheme()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        Assert.True(target.Exists(Path.Combine("themes", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Replace_Themes_DropsLocalOnlyTheme()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.False(target.Exists(Path.Combine("themes", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Merge_Connections_MatchesProfilesById()
    {
        string sharedId = "11111111-1111-1111-1111-111111111111";
        string bundleOnlyId = "22222222-2222-2222-2222-222222222222";
        string localOnlyId = "33333333-3333-3333-3333-333333333333";

        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"schemaVersion":1,"profiles":[
              {"Id":"{{sharedId}}","Name":"From Bundle","Host":"bundle.example"},
              {"Id":"{{bundleOnlyId}}","Name":"Bundle Only","Host":"only.example"}]}
            """);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"schemaVersion":1,"profiles":[
              {"Id":"{{sharedId}}","Name":"Local Version","Host":"local.example"},
              {"Id":"{{localOnlyId}}","Name":"Local Only","Host":"keep.example"}]}
            """);

        string bundle = ExportFrom(source);
        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        using var doc = target.ReadJson(Path.Combine("ssh", "profiles.json"));
        var profiles = doc.RootElement.GetProperty("profiles").EnumerateArray().ToArray();

        Assert.Equal(3, profiles.Length);
        Assert.Equal(
            "From Bundle",
            profiles.Single(p => p.GetProperty("Id").GetString() == sharedId).GetProperty("Name").GetString());
        Assert.Contains(profiles, p => p.GetProperty("Id").GetString() == localOnlyId);
        Assert.Contains(profiles, p => p.GetProperty("Id").GetString() == bundleOnlyId);
    }

    [Fact]
    public void Replace_Connections_DropsLocalOnlyProfiles()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "profiles.json"), """
            {"schemaVersion":1,"profiles":[{"Id":"22222222-2222-2222-2222-222222222222","Name":"Bundle Only"}]}
            """);
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "profiles.json"), """
            {"schemaVersion":1,"profiles":[{"Id":"33333333-3333-3333-3333-333333333333","Name":"Local Only"}]}
            """);
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        using var doc = target.ReadJson(Path.Combine("ssh", "profiles.json"));
        var profiles = doc.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Single(profiles);
        Assert.Equal("Bundle Only", profiles[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void Import_LeavesCategoriesAbsentFromBundleUntouched()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("policy", "workspace_policy.json"), """{"local":true}""");

        string bundle = Path.Combine(source.Root, "themes-only.novabackup");
        new BackupService(source.Root, Clock()).Export(bundle, new[] { BackupCategory.Themes });

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.Equal("""{"local":true}""", target.ReadFile(Path.Combine("policy", "workspace_policy.json")));
    }

    /// <summary>
    /// A bundle carries no secret material, so an imported SSH profile looks complete but
    /// cannot authenticate. Every caller that only sees the outcome string — the CLI above all —
    /// must be told, or the failure is silent.
    /// </summary>
    [Fact]
    public void Import_WithConnections_OutcomeMentionsMissingPasswords()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains("passwords", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_WithoutConnections_OutcomeOmitsPasswordNote()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(source.Root, "themes-only.novabackup");
        new BackupService(source.Root, Clock()).Export(bundle, new[] { BackupCategory.Themes });

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.DoesNotContain("passwords", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_TakesPreImportSnapshotBeforeWriting()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":77}""");
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        var service = new BackupService(target.Root, Clock());

        service.Import(bundle, ImportMode.Replace);

        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreImport);
    }

    [Fact]
    public void Import_RejectsCorruptBundleWithoutTouchingDisk()
    {
        using var target = BackupTestTree.CreatePopulated();
        string original = target.ReadFile("settings.json");
        string bogus = Path.Combine(target.Root, "bogus.novabackup");
        File.WriteAllText(bogus, "not a zip");

        var service = new BackupService(target.Root, Clock());
        var outcome = service.Import(bogus, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.CorruptArchive, outcome.Failure);
        Assert.Equal(original, target.ReadFile("settings.json"));
        // Validation happens before anything is touched, so no snapshot was needed.
        Assert.Empty(service.ListSnapshots());
    }

    [Fact]
    public void Import_RejectsNewerSchemaWithoutTouchingDisk()
    {
        using var target = BackupTestTree.CreatePopulated();
        string original = target.ReadFile("settings.json");
        string future = Path.Combine(target.Root, "future.novabackup");
        WriteFutureSchemaBundle(future);

        var outcome = new BackupService(target.Root, Clock()).Import(future, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.UnsupportedSchemaVersion, outcome.Failure);
        Assert.Equal(original, target.ReadFile("settings.json"));
    }

    /// <summary>
    /// A mid-write failure must still leave a pre-import snapshot to roll back to. A directory
    /// parked on a destination file path blocks the write on every OS — unlike a FileShare.None
    /// lock, which does not block rename or delete on POSIX.
    /// </summary>
    [Fact]
    public void Import_FailingMidWrite_LeavesPreImportSnapshot()
    {
        using var source = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        var service = new BackupService(target.Root, Clock());

        // Park a directory where a theme file must be written.
        string blocked = Path.Combine(target.Root, "themes", "solarized.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var outcome = service.Import(bundle, ImportMode.Merge);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreImport);
    }

    [Fact]
    public void Restore_RollsBackChangedFile()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        tree.WriteFile("settings.json", """{"FontSize":14}""");
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":99}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        var outcome = service.Restore(snapshot!.Id);

        Assert.True(outcome.Success, outcome.Message);
        // Restore is a Replace, which copies the file verbatim — assert on the parsed value
        // rather than formatting, so the test survives a serializer change.
        using var restored = tree.ReadJson("settings.json");
        Assert.Equal(14, restored.RootElement.GetProperty("FontSize").GetInt32());
    }

    [Fact]
    public void Restore_TakesPreRestoreSnapshotFirst()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        var snapshot = service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":99}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        service.Restore(snapshot!.Id);

        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreRestore);
    }

    [Fact]
    public void Restore_DropsLocalOnlyItems()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        var snapshot = service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile(Path.Combine("themes", "added-later.json"), """{"name":"Later"}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        service.Restore(snapshot!.Id);

        Assert.False(tree.Exists(Path.Combine("themes", "added-later.json")));
    }

    [Fact]
    public void Restore_UnknownIdFails()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());

        var outcome = service.Restore("auto-19700101T000000Z-deadbeef");

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.NotFound, outcome.Failure);
    }

    private static string ExportFrom(BackupTestTree tree)
    {
        string bundle = Path.Combine(tree.Root, "export.novabackup");
        var outcome = new BackupService(tree.Root, Clock()).Export(bundle);
        Assert.True(outcome.Success, outcome.Message);
        return bundle;
    }

    private static FixedTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));

    private static void WriteFutureSchemaBundle(string path)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(
            $$"""{"schemaVersion":{{BackupManifest.CurrentSchemaVersion + 1}},"appVersion":"9.9.9","createdUtc":"2030-01-01T00:00:00+00:00","machine":"F","categories":["settings"]}""");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupImportTests"
```

Expected: compile failure — `BackupService.Import` and `BackupService.Restore` do not exist.

- [ ] **Step 3: Add Import and Restore to BackupService**

Add these `using` directives at the top of `src/NovaTerminal.App/Shell/Backup/BackupService.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
```

Add the following members to the `BackupService` class:

```csharp
    /// <summary>
    /// Applies a bundle to the live tree. Extracts to a staging directory first and commits
    /// only once every category succeeded, so a mid-import failure leaves the original intact.
    /// </summary>
    /// <param name="categories">Null means every category the bundle contains.</param>
    public BackupOutcome Import(
        string bundlePath,
        ImportMode mode,
        IReadOnlyCollection<BackupCategory>? categories = null)
    {
        var inspection = Inspect(bundlePath);
        if (!inspection.Success)
        {
            return BackupOutcome.Fail(inspection.Failure, inspection.Message);
        }

        var available = inspection.Inspection!.Manifest.Categories
            .Select(name => Enum.TryParse<BackupCategory>(name, ignoreCase: true, out var parsed)
                ? (BackupCategory?)parsed
                : null)
            .OfType<BackupCategory>()
            .ToArray();

        var selected = (categories is null ? available : available.Intersect(categories)).ToArray();
        if (selected.Length == 0)
        {
            return BackupOutcome.Ok("Nothing to import — the bundle has none of the requested categories.");
        }

        Snapshot(SnapshotReason.PreImport);

        string staging = Path.Combine(Path.GetTempPath(), $"nova_import_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
            BundleReader.ExtractTo(bundlePath, staging, selected);

            foreach (var category in selected)
            {
                ApplyCategory(category, staging, mode);
            }

            // Name the credential gap in the outcome itself. Bundles carry no secret material,
            // so imported SSH profiles look complete but cannot authenticate until the user
            // re-enters passwords — a silent partial failure if nothing says so. The Settings
            // page has copy for this; the CLI and any other caller only ever see this string.
            string credentialNote = selected.Contains(BackupCategory.Connections)
                ? " Connection passwords are not included in a bundle — re-enter them on first connect."
                : string.Empty;

            return BackupOutcome.Ok($"Imported {selected.Length} categories ({mode}).{credentialNote}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                $"Import failed: {ex.Message}. A pre-import snapshot was taken — restore it to roll back.");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Rolls the live tree back to a snapshot. Always a Replace of the categories the snapshot
    /// contains — a rollback, not a merge. Categories absent from the snapshot are untouched.
    /// </summary>
    public BackupOutcome Restore(string snapshotId)
    {
        var snapshot = ListSnapshots().FirstOrDefault(s =>
            string.Equals(s.Id, snapshotId, StringComparison.OrdinalIgnoreCase));

        if (snapshot is null)
        {
            return BackupOutcome.Fail(BackupFailureKind.NotFound, $"No snapshot with id '{snapshotId}'.");
        }

        Snapshot(SnapshotReason.PreRestore);
        return Import(snapshot.FilePath, ImportMode.Replace);
    }

    private void ApplyCategory(BackupCategory category, string staging, ImportMode mode)
    {
        switch (category)
        {
            case BackupCategory.Settings when mode == ImportMode.Merge:
                MergeJsonObjectFile(
                    Path.Combine(staging, "settings.json"),
                    Path.Combine(RootDirectory, "settings.json"));
                break;

            case BackupCategory.Connections when mode == ImportMode.Merge:
                MergeProfilesFile(
                    Path.Combine(staging, "ssh", "profiles.json"),
                    Path.Combine(RootDirectory, "ssh", "profiles.json"));
                MergeJsonArrayFile(
                    Path.Combine(staging, "ssh", "native_known_hosts.json"),
                    Path.Combine(RootDirectory, "ssh", "native_known_hosts.json"));
                break;

            default:
                foreach (var entry in BackupCatalog.EntriesFor(category))
                {
                    string stagedPath = Path.Combine(staging, entry.SourceRelativePath);
                    string livePath = Path.Combine(RootDirectory, entry.SourceRelativePath);

                    if (entry.IsDirectory)
                    {
                        ApplyDirectory(stagedPath, livePath, mode);
                    }
                    else if (File.Exists(stagedPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                        AtomicFile.WriteAllBytes(livePath, File.ReadAllBytes(stagedPath));
                    }
                }
                break;
        }
    }

    /// <summary>Merge copies file-by-file; replace clears the live directory first.</summary>
    private static void ApplyDirectory(string stagedDirectory, string liveDirectory, ImportMode mode)
    {
        if (!Directory.Exists(stagedDirectory)) return;

        if (mode == ImportMode.Replace && Directory.Exists(liveDirectory))
        {
            Directory.Delete(liveDirectory, recursive: true);
        }

        Directory.CreateDirectory(liveDirectory);

        foreach (string staged in Directory.GetFiles(stagedDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(stagedDirectory, staged);
            string live = Path.Combine(liveDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            AtomicFile.WriteAllBytes(live, File.ReadAllBytes(staged));
        }
    }

    /// <summary>
    /// Key-by-key merge of two JSON objects, bundle winning per key. Operates on
    /// <see cref="JsonNode"/> rather than a typed model so unknown and future keys survive and
    /// settings.json keeps its PascalCase names.
    /// </summary>
    private static void MergeJsonObjectFile(string stagedPath, string livePath)
    {
        if (!File.Exists(stagedPath)) return;

        var incoming = JsonNode.Parse(File.ReadAllText(stagedPath)) as JsonObject;
        if (incoming is null) return;

        JsonObject merged;
        if (File.Exists(livePath) && JsonNode.Parse(File.ReadAllText(livePath)) is JsonObject existing)
        {
            merged = existing;
            foreach (var pair in incoming)
            {
                merged[pair.Key] = pair.Value?.DeepClone();
            }
        }
        else
        {
            merged = incoming;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        AtomicFile.WriteAllText(livePath, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Merges <c>profiles.json</c>'s profile array by <c>Id</c>, bundle winning on conflict.</summary>
    private static void MergeProfilesFile(string stagedPath, string livePath)
    {
        if (!File.Exists(stagedPath)) return;

        var incoming = JsonNode.Parse(File.ReadAllText(stagedPath)) as JsonObject;
        if (incoming is null) return;

        if (!File.Exists(livePath) || JsonNode.Parse(File.ReadAllText(livePath)) is not JsonObject existing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            AtomicFile.WriteAllText(livePath, incoming.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var byId = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Absorb(JsonObject document)
        {
            if (document["profiles"] is not JsonArray array) return;
            foreach (var element in array)
            {
                string? id = element?["Id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || element is null) continue;
                if (!byId.ContainsKey(id)) order.Add(id);
                byId[id] = element.DeepClone();
            }
        }

        Absorb(existing);
        Absorb(incoming); // bundle wins: absorbed second

        var merged = new JsonArray();
        foreach (string id in order) merged.Add(byId[id]);
        existing["profiles"] = merged;

        AtomicFile.WriteAllText(livePath, existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Union of two JSON arrays, deduped by each element's serialized form.</summary>
    private static void MergeJsonArrayFile(string stagedPath, string livePath)
    {
        if (!File.Exists(stagedPath)) return;

        if (JsonNode.Parse(File.ReadAllText(stagedPath)) is not JsonArray incoming) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new JsonArray();

        void Absorb(JsonArray array)
        {
            foreach (var element in array)
            {
                string key = element?.ToJsonString() ?? "null";
                if (seen.Add(key)) merged.Add(element?.DeepClone());
            }
        }

        if (File.Exists(livePath) && JsonNode.Parse(File.ReadAllText(livePath)) is JsonArray existing)
        {
            Absorb(existing);
        }

        Absorb(incoming);

        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        AtomicFile.WriteAllText(livePath, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
```

`AtomicFile` is `internal static` in namespace `NovaTerminal.Shell`, the same assembly and the parent namespace, so it resolves without an extra `using`.

- [ ] **Step 4: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupImportTests"
```

Expected: PASS, 18 tests.

- [ ] **Step 5: Re-run the whole Backup namespace to confirm no regression**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~NovaTerminal.Tests.Backup"
```

Expected: PASS, all Backup tests.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup/BackupService.cs tests/NovaTerminal.App.Tests/Backup/BackupImportTests.cs
git commit -m "feat(backup): staged import with merge/replace semantics and snapshot restore"
```

---

### Task 6: SnapshotScheduler

**Files:**
- Create: `src/NovaTerminal.App/Shell/Backup/SnapshotScheduler.cs`
- Test: `tests/NovaTerminal.App.Tests/Backup/SnapshotSchedulerTests.cs`

**Interfaces:**
- Consumes: `BackupService` (Tasks 3–5), `BackupCatalog` (Task 1).
- Produces: `SnapshotScheduler(BackupService service, TimeSpan? debounce = null)`; `void Start()`; `void NotifyChanged()`; `Task<SnapshotInfo?> FlushAsync()`; `IDisposable` (implements `Dispose`).

**Design note:** the `FileSystemWatcher` is the production trigger, but tests drive `NotifyChanged()` + `FlushAsync()` directly. Watcher event timing is not deterministic enough to assert on, and CI agents on ubuntu have inotify limits — a test that waits on real watcher events is a flake waiting to happen.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/SnapshotSchedulerTests.cs`:

```csharp
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

    private static TimeProvider Clock() =>
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~SnapshotSchedulerTests"
```

Expected: compile failure — `SnapshotScheduler` does not exist.

- [ ] **Step 3: Create the scheduler**

Create `src/NovaTerminal.App/Shell/Backup/SnapshotScheduler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Shell.Backup;

/// <summary>
/// Watches the backed-up paths and writes one snapshot after changes go quiet.
///
/// The debounce matters: a settings save touches several files in quick succession, and each
/// one raises multiple <see cref="FileSystemWatcher"/> events. Without coalescing, a single
/// save would produce a burst of snapshots that the hash dedupe would then mostly discard —
/// wasted work on every keystroke in the settings window.
/// </summary>
public sealed class SnapshotScheduler : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(30);

    private readonly BackupService _service;
    private readonly TimeSpan _debounce;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();

    private Timer? _timer;
    private bool _pending;
    private bool _disposed;

    public SnapshotScheduler(BackupService service, TimeSpan? debounce = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>
    /// Begins watching. Best-effort: a watcher that cannot be created (missing directory,
    /// inotify limit reached on Linux) is skipped rather than failing app startup.
    /// </summary>
    public void Start()
    {
        foreach (string directory in WatchedDirectories())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;

                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                watcher.Error += (_, e) => AppLogger.Log($"[backup] watcher error: {e.GetException().Message}");

                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[backup] could not watch {directory}: {ex.Message}");
            }
        }
    }

    /// <summary>Marks a change pending and (re)starts the debounce timer.</summary>
    public void NotifyChanged()
    {
        if (_disposed) return;

        lock (_timerLock)
        {
            _pending = true;
            _timer ??= new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Writes a snapshot now if one is pending. Returns null when nothing was pending or the
    /// snapshot was deduped away. Serialized, so overlapping timer fires cannot double-write.
    /// </summary>
    public async Task<SnapshotInfo?> FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_timerLock)
            {
                if (!_pending) return null;
                _pending = false;
            }

            return _service.Snapshot(SnapshotReason.Auto);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Distinct existing directories covering every catalog entry.</summary>
    private IEnumerable<string> WatchedDirectories()
    {
        var directories = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var entry in BackupCatalog.Entries)
        {
            string source = BackupCatalog.ResolveSource(_service.RootDirectory, entry);
            string? directory = entry.IsDirectory ? source : Path.GetDirectoryName(source);
            if (!string.IsNullOrEmpty(directory)) directories.Add(directory);
        }

        return directories;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // Never let a snapshot write re-trigger the scheduler.
        if (e.FullPath.Contains(Path.Combine(_service.RootDirectory, "backups"), StringComparison.Ordinal)) return;

        NotifyChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var watcher in _watchers)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
        }

        _watchers.Clear();

        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _gate.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~SnapshotSchedulerTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup/SnapshotScheduler.cs tests/NovaTerminal.App.Tests/Backup/SnapshotSchedulerTests.cs
git commit -m "feat(backup): debounced snapshot scheduler"
```

---

### Task 7: CLI verbs

**Files:**
- Create: `src/NovaTerminal.App/Shell/Backup/BackupCommand.cs`
- Modify: `src/NovaTerminal.Cli/Program.cs`
- Test: `tests/NovaTerminal.App.Tests/Backup/BackupCommandTests.cs`

**Interfaces:**
- Consumes: `BackupService` (Tasks 3–5).
- Produces: `BackupCommand.IsSupportedCliMode(string[] args)`; `BackupCommand.Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)` returning an exit code (0 success, 1 operation failure, 2 usage error).

**Pattern to follow:** `src/NovaTerminal.App/Shell/SshAskPassCommand.cs` — a static class with exactly those two entry points, dispatched from `Program.Main`.

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Backup/BackupCommandTests.cs`:

```csharp
using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupCommandTests
{
    [Fact]
    public void IsSupportedCliMode_RecognizesBackupVerb()
    {
        Assert.True(BackupCommand.IsSupportedCliMode(new[] { "backup", "list" }));
        Assert.False(BackupCommand.IsSupportedCliMode(new[] { "replay", "list" }));
        Assert.False(BackupCommand.IsSupportedCliMode(Array.Empty<string>()));
    }

    [Fact]
    public void Export_WritesBundleAndReportsPath()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        var (code, stdout, _) = Run(tree, "backup", "export", bundle);

        Assert.Equal(0, code);
        Assert.True(File.Exists(bundle));
        Assert.Contains("cli.novabackup", stdout);
    }

    [Fact]
    public void List_PrintsIdReasonAndSize()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var snapshot = new BackupService(tree.Root).Snapshot(SnapshotReason.Auto);

        var (code, stdout, _) = Run(tree, "backup", "list");

        Assert.Equal(0, code);
        Assert.Contains(snapshot!.Id, stdout);
        Assert.Contains("auto", stdout);
    }

    [Fact]
    public void List_WithNoSnapshots_SucceedsWithMessage()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, stdout, _) = Run(tree, "backup", "list");

        Assert.Equal(0, code);
        Assert.Contains("No snapshots", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_RequiresAModeFlag()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        new BackupService(tree.Root).Export(bundle);

        var (code, _, stderr) = Run(tree, "backup", "import", bundle);

        Assert.Equal(2, code);
        Assert.Contains("--merge", stderr);
        Assert.Contains("--replace", stderr);
    }

    [Fact]
    public void Import_RejectsBothModeFlags()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        new BackupService(tree.Root).Export(bundle);

        var (code, _, stderr) = Run(tree, "backup", "import", bundle, "--merge", "--replace");

        Assert.Equal(2, code);
        Assert.Contains("mutually exclusive", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_WithReplace_Succeeds()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":33}""");
        string bundle = Path.Combine(source.Root, "cli.novabackup");
        new BackupService(source.Root).Export(bundle);

        using var target = BackupTestTree.CreatePopulated();
        var (code, _, stderr) = Run(target, "backup", "import", bundle, "--replace");

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        Assert.Contains("33", target.ReadFile("settings.json"));
    }

    [Fact]
    public void Restore_UnknownId_ReturnsOne()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup", "restore", "auto-19700101T000000Z-deadbeef");

        Assert.Equal(1, code);
        Assert.Contains("deadbeef", stderr);
    }

    [Fact]
    public void UnknownSubcommand_ReturnsUsageError()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup", "frobnicate");

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Code, string Stdout, string Stderr) Run(BackupTestTree tree, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = BackupCommand.Execute(args, stdout, stderr, tree.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupCommandTests"
```

Expected: compile failure — `BackupCommand` does not exist.

- [ ] **Step 3: Create the command**

Create `src/NovaTerminal.App/Shell/Backup/BackupCommand.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NovaTerminal.Shell.Backup;

/// <summary>
/// The <c>backup</c> CLI verb. Follows the same shape as <see cref="SshAskPassCommand"/>:
/// a static <c>IsSupportedCliMode</c> / <c>Execute</c> pair dispatched from Program.Main.
///
/// Exit codes: 0 success, 1 the operation failed, 2 the command line was wrong.
/// </summary>
public static class BackupCommand
{
    private const string Usage = """
        Usage:
          backup export <path>
          backup import <path> --merge | --replace
          backup list
          backup restore <id>
        """;

    public static bool IsSupportedCliMode(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase);

    /// <param name="rootOverride">Test seam. Null uses <see cref="AppPaths.RootDirectory"/>.</param>
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)
    {
        if (args.Length < 2)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        var service = new BackupService(rootOverride ?? AppPaths.RootDirectory);

        return args[1].ToLowerInvariant() switch
        {
            "export" => Export(args, stdout, stderr, service),
            "import" => Import(args, stdout, stderr, service),
            "list" => List(stdout, service),
            "restore" => Restore(args, stdout, stderr, service),
            _ => Fail(stderr, Usage)
        };
    }

    private static int Export(string[] args, TextWriter stdout, TextWriter stderr, BackupService service)
    {
        if (args.Length < 3) return Fail(stderr, Usage);

        var outcome = service.Export(args[2]);
        if (!outcome.Success)
        {
            stderr.WriteLine(outcome.Message);
            return 1;
        }

        stdout.WriteLine(outcome.Message);
        return 0;
    }

    private static int Import(string[] args, TextWriter stdout, TextWriter stderr, BackupService service)
    {
        if (args.Length < 3) return Fail(stderr, Usage);

        bool merge = args.Any(a => string.Equals(a, "--merge", StringComparison.OrdinalIgnoreCase));
        bool replace = args.Any(a => string.Equals(a, "--replace", StringComparison.OrdinalIgnoreCase));

        if (merge && replace)
        {
            return Fail(stderr, "--merge and --replace are mutually exclusive.\n" + Usage);
        }

        if (!merge && !replace)
        {
            // No default: guessing wrong here overwrites the user's configuration.
            return Fail(stderr, "Specify --merge or --replace.\n" + Usage);
        }

        var outcome = service.Import(args[2], merge ? ImportMode.Merge : ImportMode.Replace);
        if (!outcome.Success)
        {
            stderr.WriteLine(outcome.Message);
            return 1;
        }

        stdout.WriteLine(outcome.Message);
        return 0;
    }

    private static int List(TextWriter stdout, BackupService service)
    {
        var snapshots = service.ListSnapshots();
        if (snapshots.Count == 0)
        {
            stdout.WriteLine("No snapshots yet.");
            return 0;
        }

        foreach (var snapshot in snapshots)
        {
            stdout.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1,-11}  {2}  {3,8:N0} bytes",
                snapshot.Id,
                ReasonLabel(snapshot.Reason),
                snapshot.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                snapshot.SizeBytes));
        }

        return 0;
    }

    private static int Restore(string[] args, TextWriter stdout, TextWriter stderr, BackupService service)
    {
        if (args.Length < 3) return Fail(stderr, Usage);

        var outcome = service.Restore(args[2]);
        if (!outcome.Success)
        {
            stderr.WriteLine(outcome.Message);
            return 1;
        }

        stdout.WriteLine(outcome.Message);
        return 0;
    }

    private static string ReasonLabel(SnapshotReason reason) => reason switch
    {
        SnapshotReason.Auto => "auto",
        SnapshotReason.PreImport => "pre-import",
        SnapshotReason.PreRestore => "pre-restore",
        _ => "auto"
    };

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return 2;
    }
}
```

- [ ] **Step 4: Wire it into the CLI**

In `src/NovaTerminal.Cli/Program.cs`, add `using NovaTerminal.Shell.Backup;` at the top and insert this branch after the `ReplayCommand` branch, before the "Unsupported CLI mode." fallback:

```csharp
        if (BackupCommand.IsSupportedCliMode(args))
        {
            return BackupCommand.Execute(args, Console.Out, Console.Error);
        }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~BackupCommandTests"
```

Expected: PASS, 9 tests.

- [ ] **Step 6: Verify the CLI project still builds**

```bash
scripts/build.ps1 build src/NovaTerminal.Cli
```

Expected: build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/NovaTerminal.App/Shell/Backup/BackupCommand.cs src/NovaTerminal.Cli/Program.cs tests/NovaTerminal.App.Tests/Backup/BackupCommandTests.cs
git commit -m "feat(backup): backup export/import/list/restore CLI verbs"
```

---

### Task 8: Settings window "Backup & Restore" page

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml`
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs`

**Interfaces:**
- Consumes: `BackupService` (Tasks 3–5).
- Produces: no new public API. Named controls: `DataNav`, `BtnBackupExport`, `BtnBackupImport`, `BackupStatusText`, `SnapshotList`, `BtnRestoreSnapshot`.

**Critical gotcha — read before editing.** `SettingsWindow.axaml.cs` around line 141 documents it: the sidebar `ListBox`es are the navigation, not the tab strip. `InterfaceNav` owns tabs 0–2, `AssistantNav` owns 3–4, `ConnectionNav` owns 5. **A new tab MUST get a sidebar item and a mapping in both the `SelectionChanged` handler and `SyncSidebarFromTabs`, or it is unreachable** — the SSH tab shipped without one and silently remapped "Agent Access" onto SSH. **Add the new `TabItem` at the END of the `TabControl`** (index 6) so existing offsets stay true.

Also note: `MainWindow.SetupCommandPalette()` runs on palette-open and on settings-save, not at startup.

- [ ] **Step 1: Add the sidebar group to the AXAML**

In `src/NovaTerminal.App/SettingsWindow.axaml`, after the `ConnectionNav` `ListBox`'s closing `</ListBox>` tag, add:

```xml
                        <TextBlock Classes="SectionHeader" Text="DATA" Margin="14,18,14,8"/>
                        <ListBox Name="DataNav"
                                 Classes="SideNav"
                                 SelectionMode="Single">
                            <ListBoxItem>
                                <Grid ColumnDefinitions="20,*">
                                    <TextBlock Grid.Column="0" Text="⇩" FontSize="13" Foreground="{StaticResource NtFg3}" VerticalAlignment="Center"/>
                                    <TextBlock Grid.Column="1" Text="Backup &amp; Restore" Margin="6,0,0,0" VerticalAlignment="Center"/>
                                </Grid>
                            </ListBoxItem>
                        </ListBox>
```

- [ ] **Step 2: Add the tab page at the END of the TabControl**

Find the `TabControl` and add this as the **last** `TabItem`, after the existing final one. Match the surrounding tabs' structure (each wraps content in a `ScrollViewer` + `StackPanel`); if the existing tabs use a different wrapper, copy theirs rather than this skeleton.

```xml
            <TabItem Header="Backup">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="24" Spacing="0">
                        <TextBlock Classes="SectionHeader" Text="BACKUP &amp; RESTORE" Margin="0,0,0,14"/>

                        <TextBlock Classes="RowDesc"
                                   TextWrapping="Wrap"
                                   Margin="0,0,0,14"
                                   Text="Export your settings, themes, connections, workspaces, and snippets to a single file you can carry to another machine. Passwords are never included — they stay in your operating system's keychain, and you re-enter them once after importing."/>

                        <StackPanel Orientation="Horizontal" Spacing="8" Margin="0,0,0,10">
                            <Button Name="BtnBackupExport" Classes="Pill" Content="Export…"/>
                            <Button Name="BtnBackupImport" Classes="Pill" Content="Import…"/>
                        </StackPanel>

                        <TextBlock Name="BackupStatusText" Text="" Foreground="{StaticResource NtGreen}" FontSize="11" Margin="0,0,0,18"/>

                        <TextBlock Classes="SectionHeader" Text="SNAPSHOTS" Margin="0,0,0,8"/>
                        <TextBlock Classes="RowDesc"
                                   TextWrapping="Wrap"
                                   Margin="0,0,0,10"
                                   Text="Automatic local copies, taken after your configuration changes and before every import or restore. Restoring replaces the categories a snapshot contains."/>

                        <ListBox Name="SnapshotList" Height="200" Margin="0,0,0,10"/>
                        <Button Name="BtnRestoreSnapshot" Classes="Pill" Content="Restore selected" HorizontalAlignment="Left"/>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
```

- [ ] **Step 3: Extend the nav dispatcher**

In `src/NovaTerminal.App/SettingsWindow.axaml.cs`, in the block that resolves the nav list boxes (around line 152), add `DataNav` and its mapping. Replace:

```csharp
            var interfaceNav = this.FindControl<ListBox>("InterfaceNav");
            var assistantNav = this.FindControl<ListBox>("AssistantNav");
            var connectionNav = this.FindControl<ListBox>("ConnectionNav");
            if (tabs != null && interfaceNav != null && assistantNav != null && connectionNav != null)
            {
                tabs.SelectionChanged += (_, _) => SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav);
```

with:

```csharp
            var interfaceNav = this.FindControl<ListBox>("InterfaceNav");
            var assistantNav = this.FindControl<ListBox>("AssistantNav");
            var connectionNav = this.FindControl<ListBox>("ConnectionNav");
            var dataNav = this.FindControl<ListBox>("DataNav");
            if (tabs != null && interfaceNav != null && assistantNav != null && connectionNav != null && dataNav != null)
            {
                tabs.SelectionChanged += (_, _) => SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav, dataNav);
```

Then add the `DataNav` handler alongside the `connectionNav` one:

```csharp
                dataNav.SelectionChanged += (_, _) =>
                {
                    if (dataNav.SelectedIndex < 0) return;
                    tabs.SelectedIndex = dataNav.SelectedIndex + 6;
                };
```

and update the final call in that block:

```csharp
                SyncSidebarFromTabs(tabs, interfaceNav, assistantNav, connectionNav, dataNav);
```

- [ ] **Step 4: Extend SyncSidebarFromTabs**

Replace the whole `SyncSidebarFromTabs` method (around line 918) with:

```csharp
        /// <summary>
        /// Mirror the current tab control selection into the sidebar list boxes.
        /// InterfaceNav owns tabs 0-2 (Appearance / Profiles / Shortcuts), AssistantNav owns
        /// tabs 3-4 (Command Assist / Agent Access), ConnectionNav owns tab 5 (SSH), DataNav
        /// owns tab 6 (Backup & Restore). The other list boxes are cleared so only one item
        /// ever reads as selected.
        /// </summary>
        private static void SyncSidebarFromTabs(
            TabControl tabs,
            ListBox interfaceNav,
            ListBox assistantNav,
            ListBox connectionNav,
            ListBox dataNav)
        {
            var idx = tabs.SelectedIndex;

            interfaceNav.SelectedIndex = -1;
            assistantNav.SelectedIndex = -1;
            connectionNav.SelectedIndex = -1;
            dataNav.SelectedIndex = -1;

            if (idx < 0) return;

            if (idx < 3) interfaceNav.SelectedIndex = idx;
            else if (idx < 5) assistantNav.SelectedIndex = idx - 3;
            else if (idx < 6) connectionNav.SelectedIndex = idx - 5;
            else dataNav.SelectedIndex = idx - 6;
        }
```

- [ ] **Step 5: Wire the buttons**

Add `using NovaTerminal.Shell.Backup;` at the top of `SettingsWindow.axaml.cs`, and add a call to `WireBackupSection();` at the end of the same constructor region that wires the other buttons. Then add the method:

```csharp
        /// <summary>
        /// Wires the Backup &amp; Restore page. All work goes through <see cref="BackupService"/>;
        /// this method only picks files and renders outcomes.
        /// </summary>
        private void WireBackupSection()
        {
            var service = new BackupService(AppPaths.RootDirectory);

            var btnExport = this.FindControl<Button>("BtnBackupExport");
            var btnImport = this.FindControl<Button>("BtnBackupImport");
            var btnRestore = this.FindControl<Button>("BtnRestoreSnapshot");
            var status = this.FindControl<TextBlock>("BackupStatusText");
            var snapshotList = this.FindControl<ListBox>("SnapshotList");

            void SetStatus(string message, bool success)
            {
                if (status is null) return;
                status.Text = message;
                status.Foreground = success
                    ? (Avalonia.Media.IBrush?)this.FindResource("NtGreen")
                    : (Avalonia.Media.IBrush?)this.FindResource("NtRed");
            }

            void RefreshSnapshots()
            {
                if (snapshotList is null) return;

                var rows = service.ListSnapshots()
                    .Select(s => new SnapshotRow(
                        s.Id,
                        $"{s.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm}  ·  {ReasonLabel(s.Reason)}  ·  {s.SizeBytes / 1024.0:N0} KB"))
                    .ToArray();

                snapshotList.ItemsSource = rows;
                snapshotList.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(SnapshotRow.Display));
            }

            RefreshSnapshots();

            if (btnExport != null)
            {
                btnExport.Click += async (_, _) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel is null) return;

                    var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerSaveOptions
                        {
                            Title = "Export NovaTerminal configuration",
                            SuggestedFileName = $"novaterminal-{DateTime.Now:yyyy-MM-dd}{BackupService.BundleExtension}",
                            DefaultExtension = BackupService.BundleExtension.TrimStart('.')
                        });

                    if (file is null) return;

                    var outcome = service.Export(file.Path.LocalPath);
                    SetStatus(outcome.Success ? $"Exported to {file.Name}." : outcome.Message, outcome.Success);
                };
            }

            if (btnImport != null)
            {
                btnImport.Click += async (_, _) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel is null) return;

                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerOpenOptions
                        {
                            Title = "Import NovaTerminal configuration",
                            AllowMultiple = false
                        });

                    if (files.Count == 0) return;
                    string path = files[0].Path.LocalPath;

                    // Inspect first so the confirmation names what is about to change.
                    var inspection = service.Inspect(path);
                    if (!inspection.Success)
                    {
                        SetStatus(inspection.Message, success: false);
                        return;
                    }

                    var mode = await PromptForImportModeAsync(inspection.Inspection!);
                    if (mode is null) return;

                    var outcome = service.Import(path, mode.Value);
                    SetStatus(
                        outcome.Success
                            ? $"Imported ({mode}). Restart NovaTerminal to pick up all changes."
                            : outcome.Message,
                        outcome.Success);
                    RefreshSnapshots();
                };
            }

            if (btnRestore != null)
            {
                btnRestore.Click += (_, _) =>
                {
                    if (snapshotList?.SelectedItem is not SnapshotRow row)
                    {
                        SetStatus("Select a snapshot first.", success: false);
                        return;
                    }

                    var outcome = service.Restore(row.Id);
                    SetStatus(
                        outcome.Success
                            ? "Restored. Restart NovaTerminal to pick up all changes."
                            : outcome.Message,
                        outcome.Success);
                    RefreshSnapshots();
                };
            }
        }

        private static string ReasonLabel(SnapshotReason reason) => reason switch
        {
            SnapshotReason.Auto => "automatic",
            SnapshotReason.PreImport => "before import",
            SnapshotReason.PreRestore => "before restore",
            _ => "automatic"
        };

        private sealed record SnapshotRow(string Id, string Display);
```

- [ ] **Step 6: Add the import-mode prompt**

Add this method to `SettingsWindow`. It uses a plain `Window` rather than a new AXAML file — the dialog is three buttons and a summary line, so a code-built window keeps it in one place.

```csharp
        /// <summary>
        /// Asks whether to merge or replace, showing what the bundle contains. Returns null
        /// when the user cancels. Import is destructive, so there is no default —
        /// the user must pick.
        /// </summary>
        private async Task<ImportMode?> PromptForImportModeAsync(BundleInspection inspection)
        {
            string summary = string.Join(
                ", ",
                inspection.ItemCounts
                    .Where(pair => pair.Value > 0)
                    .Select(pair => $"{pair.Value} {pair.Key.ToString().ToLowerInvariant()}"));

            ImportMode? choice = null;

            var dialog = new Window
            {
                Title = "Import configuration",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var mergeButton = new Button { Content = "Merge", Classes = { "Pill" } };
            var replaceButton = new Button { Content = "Replace", Classes = { "Pill" } };
            var cancelButton = new Button { Content = "Cancel", Classes = { "Pill" } };

            mergeButton.Click += (_, _) => { choice = ImportMode.Merge; dialog.Close(); };
            replaceButton.Click += (_, _) => { choice = ImportMode.Replace; dialog.Close(); };
            cancelButton.Click += (_, _) => dialog.Close();

            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"This bundle contains: {summary}.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Merge keeps items you have locally that the bundle does not contain. " +
                               "Replace makes the bundle the truth for the categories above. " +
                               "A snapshot is taken first either way, so you can roll back.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.75
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancelButton, mergeButton, replaceButton }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return choice;
        }
```

- [ ] **Step 7: Build and verify**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: build succeeded, 0 errors. AXAML errors surface here, not at runtime.

- [ ] **Step 8: Run the full App.Tests Backup suite plus the settings tests**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Backup|FullyQualifiedName~Settings"
```

Expected: PASS. Any existing settings test that asserts a tab count or nav mapping will fail here — update it to expect 7 tabs and the `DataNav` group.

- [ ] **Step 9: Manual smoke test**

Automated GUI driving is unreliable on Windows, so verify by hand:

1. `scripts/build.ps1 build src/NovaTerminal.App`, then launch NovaTerminal.
2. Open Settings. Confirm a **DATA** group with **Backup & Restore** appears in the sidebar, and that clicking every other sidebar item still lands on its own page (this is the regression the offsets can break).
3. Click **Export…**, save a file, confirm the green status line names it.
4. Change a setting, wait ~30s, return to Backup & Restore, reopen Settings, and confirm a snapshot row appeared.
5. Click **Import…**, pick the exported file, confirm the dialog names the categories, choose **Merge**, confirm the success line.
6. Select a snapshot, click **Restore selected**, confirm success and that a "before restore" snapshot appears in the list.

- [ ] **Step 10: Commit**

```bash
git add src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs
git commit -m "feat(backup): Backup & Restore settings page"
```

---

### Task 9: Command palette entries and scheduler startup

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: `BackupService`, `SnapshotScheduler`, `CommandRegistry.Register(string title, string category, Action action, string shortcut = "", string id = "")`.
- Produces: no new public API.

**Gotcha:** `SetupCommandPalette()` runs on palette-open and on settings-save, **not** at startup. Register commands there (it is the right place — the registry is rebuilt each time), but start the `SnapshotScheduler` from the constructor's end, not from `SetupCommandPalette`, or snapshots only begin after the user first opens the palette.

- [ ] **Step 1: Register the palette commands**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, add `using NovaTerminal.Shell.Backup;` and, inside `SetupCommandPalette()` alongside the other `CommandRegistry.Register` calls, add:

```csharp
            CommandRegistry.Register(
                "Export configuration…",
                "Backup",
                () => OpenSettingsToBackupPage(),
                id: "backup.export");

            CommandRegistry.Register(
                "Import configuration…",
                "Backup",
                () => OpenSettingsToBackupPage(),
                id: "backup.import");

            CommandRegistry.Register(
                "Restore from snapshot…",
                "Backup",
                () => OpenSettingsToBackupPage(),
                id: "backup.restore");
```

All three route to the same page because export and import both need a file picker and a mode prompt, which already live there. Duplicating that flow into the palette would mean two copies of the confirmation logic — and the confirmation is the part that must not drift.

- [ ] **Step 2: Add the navigation helper**

Add to `MainWindow`, next to the existing settings-opening code (find how the window currently opens `SettingsWindow` and match it — it may already have a helper that takes an initial tab):

```csharp
        /// <summary>
        /// Opens Settings on the Backup &amp; Restore page (the last tab; see the nav-offset
        /// comment in SettingsWindow).
        /// </summary>
        private void OpenSettingsToBackupPage()
        {
            var window = new SettingsWindow(_settings);
            window.SelectBackupPage();
            window.ShowDialog(this);
        }
```

If `SettingsWindow`'s constructor signature differs, match the existing call site rather than this sketch.

- [ ] **Step 3: Add SelectBackupPage to SettingsWindow**

In `src/NovaTerminal.App/SettingsWindow.axaml.cs`:

```csharp
        /// <summary>Selects the Backup &amp; Restore tab. Used by the command palette entries.</summary>
        public void SelectBackupPage()
        {
            var tabs = this.FindControl<TabControl>("SettingsTabs");
            if (tabs is null) return;

            // Backup is the last tab by construction — new tabs go at the end so the
            // sidebar offsets stay true.
            tabs.SelectedIndex = tabs.Items.Count - 1;
        }
```

Check the actual `TabControl`'s `Name` in the AXAML and use it; if it has none, add `Name="SettingsTabs"`.

- [ ] **Step 4: Start the snapshot scheduler**

At the **end of the MainWindow constructor** (not in `SetupCommandPalette`), add:

```csharp
            // Snapshot scheduling starts with the app, not with the first palette open.
            _snapshotScheduler = new SnapshotScheduler(new BackupService(AppPaths.RootDirectory));
            _snapshotScheduler.Start();
```

with the field:

```csharp
        private SnapshotScheduler? _snapshotScheduler;
```

and disposal wherever `MainWindow` already tears down its services (its `OnClosed` override or equivalent):

```csharp
            _snapshotScheduler?.Dispose();
            _snapshotScheduler = null;
```

- [ ] **Step 5: Build and verify**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: build succeeded, 0 errors.

- [ ] **Step 6: Run the App.Tests suite**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Backup|FullyQualifiedName~CommandPalette"
```

Expected: PASS. If a palette-ordering test asserts an exact command count, update it for the three new entries.

- [ ] **Step 7: Manual smoke test**

1. Launch NovaTerminal, open the command palette.
2. Type "backup" — confirm all three entries appear under a Backup category.
3. Pick "Restore from snapshot…" and confirm Settings opens on the Backup & Restore page.

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/SettingsWindow.axaml.cs
git commit -m "feat(backup): palette entries and snapshot scheduler startup"
```

---

### Task 10: MCP export and list tools

Read-only by design. No import, no restore: the MCP server is out-of-process and its existing tools are schema/validation helpers, so an agent silently replacing live connection profiles is a destructive action the user never sees.

**Files:**
- Create: `src/NovaTerminal.McpServer/Tools/BackupTools.cs`
- Test: `tests/NovaTerminal.McpServer.Tests/BackupToolsTests.cs`

**Interfaces:**
- Consumes: nothing from `NovaTerminal.App` — check whether `NovaTerminal.McpServer.csproj` references it. If it does not, **do not add the reference**; instead reimplement the two operations against the same zip format by referencing the shared logic, or move `Shell/Backup/` to `NovaTerminal.Platform` first. Confirm before writing code:

```bash
grep -n "ProjectReference" src/NovaTerminal.McpServer/NovaTerminal.McpServer.csproj
```

- Produces: MCP tools `novaterminal.backup_export` and `novaterminal.backup_list`.

**Pattern to follow:** `src/NovaTerminal.McpServer/Tools/SettingsTools.cs` — `[McpServerToolType]` on a static class, `[McpServerTool(Name = "...")]` plus `[Description(...)]` on each static method, returning a string.

- [ ] **Step 1: Confirm the project reference situation**

```bash
grep -n "ProjectReference" src/NovaTerminal.McpServer/NovaTerminal.McpServer.csproj
```

If `NovaTerminal.App` is **not** referenced, stop and move `src/NovaTerminal.App/Shell/Backup/` into `src/NovaTerminal.Platform/Backup/` (namespace `NovaTerminal.Platform.Backup`), update the `using` directives in every file from Tasks 1–9, re-run the whole Backup suite, and commit that move as its own commit before continuing. `AtomicFile` is `internal` to `NovaTerminal.App`, so the move also needs an equivalent in Platform — `JsonSshProfileStore` already has the same temp-write-then-move pattern; extract or duplicate it there.

- [ ] **Step 2: Write the failing test**

Create `tests/NovaTerminal.McpServer.Tests/BackupToolsTests.cs`. Match the existing tests' namespace and helper style in that project — read one first.

```csharp
using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

public sealed class BackupToolsTests
{
    [Fact]
    public void BackupExport_WritesBundleAndReportsIt()
    {
        string root = CreateTree();
        try
        {
            string destination = Path.Combine(root, "agent-export.novabackup");

            string result = BackupTools.BackupExport(destination, root);

            Assert.True(File.Exists(destination));
            Assert.Contains("agent-export.novabackup", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BackupExport_ReportsFailureAsText()
    {
        string root = CreateTree();
        try
        {
            string blocked = Path.Combine(root, "blocked.novabackup");
            Directory.CreateDirectory(blocked);

            string result = BackupTools.BackupExport(blocked, root);

            Assert.Contains("Could not write", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BackupList_WithNoSnapshots_SaysSo()
    {
        string root = CreateTree();
        try
        {
            string result = BackupTools.BackupList(root);
            Assert.Contains("No snapshots", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nova_mcp_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), """{"FontSize":14}""");
        return root;
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~BackupToolsTests"
```

Expected: compile failure — `BackupTools` does not exist.

- [ ] **Step 4: Create the tools**

Create `src/NovaTerminal.McpServer/Tools/BackupTools.cs`. Adjust the `using` for the backup namespace to whatever Step 1 settled on.

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using NovaTerminal.Shell.Backup;

namespace NovaTerminal.McpServer.Tools;

/// <summary>
/// Read-only backup tools. Export and list only — deliberately no import or restore.
/// Those replace the user's live configuration, and an out-of-process agent doing that
/// silently is a destructive action the user never sees. Export-before-you-change is the
/// useful half and carries no risk.
/// </summary>
[McpServerToolType]
public static class BackupTools
{
    [McpServerTool(Name = "novaterminal.backup_export"),
     Description("Export NovaTerminal's configuration (settings, themes, connections, workspaces, policy, snippets) " +
                 "to a .novabackup file. Passwords are never included. Use this before changing configuration " +
                 "so the user can roll back.")]
    public static string BackupExport(
        [Description("Absolute path for the .novabackup file to write.")] string destinationPath,
        [Description("App data root. Omit to use the current user's NovaTerminal directory.")] string? rootDirectory = null)
    {
        var service = new BackupService(rootDirectory ?? ResolveDefaultRoot());
        var outcome = service.Export(destinationPath);
        return outcome.Success
            ? $"Exported configuration to {destinationPath}."
            : outcome.Message;
    }

    [McpServerTool(Name = "novaterminal.backup_list"),
     Description("List NovaTerminal's automatic configuration snapshots, newest first, with id, reason, " +
                 "timestamp, and size. The user restores a snapshot from Settings > Backup & Restore.")]
    public static string BackupList(
        [Description("App data root. Omit to use the current user's NovaTerminal directory.")] string? rootDirectory = null)
    {
        var snapshots = new BackupService(rootDirectory ?? ResolveDefaultRoot()).ListSnapshots();
        if (snapshots.Count == 0) return "No snapshots yet.";

        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1}  {2}  {3:N0} bytes",
                snapshot.Id,
                snapshot.Reason,
                snapshot.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                snapshot.SizeBytes));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Mirrors AppPaths.RootDirectory, including the NOVATERM_APPDATA_ROOT override, without
    /// depending on the App assembly's static initializer (which creates directories).
    /// </summary>
    private static string ResolveDefaultRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaTerminal");
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~BackupToolsTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 6: Run the McpServer suite in full**

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests
```

Expected: PASS. This project has drift-guard tests that enumerate registered tools — if one asserts an exact tool count or list, add the two new tools to it.

- [ ] **Step 7: Update the MCP README**

Add the two tools to `src/NovaTerminal.McpServer/README.md`'s tool table, matching the existing rows' format, and note that import and restore are intentionally absent.

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.McpServer tests/NovaTerminal.McpServer.Tests/BackupToolsTests.cs
git commit -m "feat(backup): read-only MCP export and list tools"
```

---

### Task 11: Full verification

**Files:** none — verification only.

- [ ] **Step 1: Run every affected test project**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Category!=ShellIntegration"
```

Expected: PASS. This mirrors the CI unit-test filter plus the `ShellIntegration` quarantine.

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests
```

Expected: PASS.

```bash
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests
```

Expected: PASS. This project enforces module boundaries — if the Task 10 namespace move happened, it is the test that catches a bad layering.

- [ ] **Step 2: Confirm the whole solution builds**

```bash
scripts/build.ps1 build
```

Expected: build succeeded, 0 errors, 0 new warnings.

- [ ] **Step 3: Verify the secret guarantee by hand**

```bash
scripts/build.ps1 build src/NovaTerminal.Cli
```

Then, in a terminal, export from a real profile and inspect the archive listing to confirm it contains only the expected categories and no `logs/`, `recordings/`, `history`, or `vault` entries.

- [ ] **Step 4: Commit any fixes and summarize**

```bash
git add -A
git commit -m "test: verify backup and restore across projects"
```

---

## Deferred (not in this plan)

- **Sync across machines** — needs its own spec: a sync target, change detection, conflict resolution. The bundle format built here is its foundation.
- **MCP import behind a settings toggle** — only if agent-driven restore turns out to be wanted.
- **Bundle schema v2 migrations** — the hook exists in `BundleReader.Open` with a comment; there is nothing to migrate at v1.
