using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaTerminal.Backup;

/// <summary>
/// Export, import, snapshot, and restore of NovaTerminal configuration.
///
/// Takes its app-data root as a constructor argument rather than reading the app's data root
/// statically, so tests drive it against a temp tree without touching the real profile.
///
/// Never reads secret storage. A bundle carries connection profiles with their
/// <c>RememberPasswordInVault</c> flag but no password material (issue #100).
/// </summary>
public sealed class BackupService
{
    public const string BundleExtension = ".novabackup";

    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _log;

    public BackupService(string rootDirectory, TimeProvider? timeProvider = null, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log ?? (static _ => { });
    }

    public string RootDirectory { get; }

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    /// <summary>Writes a bundle at <paramref name="destinationPath"/>.</summary>
    /// <param name="categories">Null means every category.</param>
    public BackupOutcome Export(string destinationPath, IReadOnlyCollection<BackupCategory>? categories = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                "Destination path must not be empty.");
        }

        // F2: both the CLI and the MCP tool accept an arbitrary destination path. If it resolves
        // onto (or under) one of the live paths BackupCatalog.Entries actually backs up - e.g.
        // "<root>/settings.json" - BundleWriter first archives that live file into its temp
        // bundle, then File.Move(overwrite: true) replaces it with ZIP bytes and Export still
        // reports success: the export "succeeds" by destroying the very configuration it was
        // asked to preserve. Caught here, before BundleWriter ever runs, rather than left to
        // surface as a corrupt settings.json the next time the app starts.
        //
        // Only the PUBLIC entry point checks this - Snapshot() below writes into
        // BackupsDirectory (one of the paths this guard rejects) via ExportCore directly,
        // deliberately bypassing it. Snapshot is this service's own internal, fully-controlled
        // write - the id it builds is unique per call (timestamp + content hash), so it can never
        // collide with an existing snapshot the way an arbitrary caller-supplied path could.
        if (TryDescribeProtectedDestination(destinationPath, out string? conflictReason))
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                $"Cannot export to '{destinationPath}': {conflictReason}");
        }

        return ExportCore(destinationPath, categories);
    }

    private BackupOutcome ExportCore(string destinationPath, IReadOnlyCollection<BackupCategory>? categories)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                $"Could not write {destinationPath}: {ex.Message}");
        }
    }

    /// <summary>Reads a bundle's manifest and item counts without extracting anything.</summary>
    public InspectOutcome Inspect(string bundlePath) => BundleReader.Open(bundlePath);

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Applies a bundle to the live tree in two phases. Phase 1 extracts the bundle and computes
    /// every category's result under a scratch directory, touching nothing live — a corrupt
    /// entry or a merge failure aborts here with the live tree untouched. Phase 2 commits each
    /// computed result with a rename-based undo journal: the current live path is renamed aside
    /// before the new content is moved into place, so a mid-commit failure (e.g. a destination
    /// blocked by something on disk) rolls every already-committed category back to exactly what
    /// it was before this call, not just "recoverable via a separate Restore."
    /// </summary>
    /// <param name="categories">Null means every category the bundle contains.</param>
    public BackupOutcome Import(
        string bundlePath,
        ImportMode mode,
        IReadOnlyCollection<BackupCategory>? categories = null)
    {
        var (earlyReturn, selected) = ValidateAndSelect(bundlePath, categories);
        if (earlyReturn is not null) return earlyReturn;

        if (Snapshot(SnapshotReason.PreImport) is null)
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                "Could not write a pre-import snapshot; refusing to import without a rollback point.");
        }

        return ImportCore(bundlePath, mode, selected!);
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

        // Copy the target bundle aside before taking the pre-restore snapshot (which prunes) or
        // touching the live tree. Otherwise pruning triggered by that very snapshot could delete
        // the snapshot being restored (a target at the retention edge gets pushed out by the new
        // one), and reading straight from BackupsDirectory risks aliasing the source bundle with
        // the live tree Import is about to overwrite.
        string tempBundle = Path.Combine(Path.GetTempPath(), $"nova_restore_{Guid.NewGuid():N}{BundleExtension}");
        try
        {
            File.Copy(snapshot.FilePath, tempBundle, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                $"Could not stage snapshot '{snapshotId}' for restore: {ex.Message}");
        }

        try
        {
            var (earlyReturn, selected) = ValidateAndSelect(tempBundle, categories: null);
            if (earlyReturn is not null) return earlyReturn;

            if (Snapshot(SnapshotReason.PreRestore) is null)
            {
                return BackupOutcome.Fail(
                    BackupFailureKind.WriteFailed,
                    "Could not write a pre-restore snapshot; refusing to restore without a rollback point.");
            }

            return ImportCore(tempBundle, ImportMode.Replace, selected!, operationNoun: "Restored");
        }
        finally
        {
            try { if (File.Exists(tempBundle)) File.Delete(tempBundle); }
            catch (Exception ex) { _log($"[backup] could not clean up restore staging copy: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Validates the bundle and resolves which categories will actually be touched. Returns a
    /// non-null <c>EarlyReturn</c> for both failure (bad bundle) and the no-op "nothing
    /// requested is present" case — either way, the caller must return it without taking a
    /// snapshot or writing anything.
    /// </summary>
    private (BackupOutcome? EarlyReturn, BackupCategory[]? Selected) ValidateAndSelect(
        string bundlePath, IReadOnlyCollection<BackupCategory>? categories)
    {
        var inspection = Inspect(bundlePath);
        if (!inspection.Success)
        {
            return (BackupOutcome.Fail(inspection.Failure, inspection.Message), null);
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
            return (BackupOutcome.Ok("Nothing to import — the bundle has none of the requested categories."), null);
        }

        return (null, selected);
    }

    /// <summary>
    /// Where Import stages its scratch tree. Deliberately a sibling of the live tree under
    /// <see cref="RootDirectory"/>, never under <see cref="Path.GetTempPath"/>: Phase 2's commit
    /// moves directories with <see cref="Directory.Move"/>, which is a bare rename and throws
    /// <see cref="IOException"/> across a volume boundary on both Windows and Unix — unlike
    /// <see cref="File.Move"/>, which falls back to copy+delete. TEMP is commonly on a different
    /// drive from an app-data root on Windows, so staging there made every directory-category
    /// import fail outright on an ordinary machine. Neither <see cref="ComputeContentHash"/> nor
    /// <see cref="HasContent"/> walk anything but <see cref="BackupCatalog.Entries"/>, so a
    /// dot-prefixed scratch directory here is invisible to both, and it is always removed before
    /// this method returns except on the (already-logged) rollback-failure path. A fresh GUID per
    /// call also means a leftover from a killed process can never be picked up or reused by a
    /// later run. <c>internal</c> rather than <c>private</c> so tests can pin the "always under
    /// RootDirectory" invariant directly, since a cross-volume TEMP regression is otherwise
    /// invisible to any test whose whole tree lives under one temp root already.
    /// </summary>
    internal string ResolveImportStagingRoot() => Path.Combine(RootDirectory, $".import-{Guid.NewGuid():N}");

    /// <summary>
    /// Test-only hook: when set, <see cref="ImportCore"/> throws this instead of running
    /// <see cref="CommitWithUndo"/>. Exists to deterministically and portably exercise the
    /// catch-all around that call (I3) — an exception type outside the set
    /// <see cref="CommitWithUndo"/>'s own per-step catch filters on cannot be coaxed out of real
    /// File/Directory APIs on demand (they only ever throw <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>, <see cref="ArgumentException"/>, or
    /// <see cref="NotSupportedException"/> and their subtypes), so this simulates the escape
    /// directly rather than relying on an environment-specific fault.
    /// </summary>
    internal Func<Exception>? SimulateCommitPhaseFailureForTest;

    /// <summary>
    /// Does the actual two-phase apply. Never snapshots — callers (<see cref="Import"/> and
    /// <see cref="Restore"/>) each take exactly one forced snapshot under their own reason before
    /// calling this, so a Restore does not also write a redundant pre-import snapshot.
    /// </summary>
    private BackupOutcome ImportCore(
        string bundlePath, ImportMode mode, BackupCategory[] selected, string operationNoun = "Imported")
    {
        string staging = ResolveImportStagingRoot();
        string extracted = Path.Combine(staging, "extracted");
        string final = Path.Combine(staging, "final");
        string undo = Path.Combine(staging, "undo");

        // Set when the automatic rollback itself could not fully restore every step — staging
        // (specifically undo/, which then holds the only surviving copy of whatever the rollback
        // could not put back) must not be deleted on that path, or the cheaper recovery route is
        // destroyed right after the failure message points at it.
        bool preserveStagingForManualRecovery = false;

        try
        {
            try
            {
                Directory.CreateDirectory(extracted);
                BundleReader.ExtractTo(bundlePath, extracted, selected);
            }
            catch (InvalidDataException ex)
            {
                // BundleReader's own zip-slip guard, or a corrupt per-entry deflate stream.
                // Inspect() only reads the manifest and central directory, so this is the first
                // point either can surface — and it must come back as a typed failure, not an
                // exception escaping Import/Restore.
                return BackupOutcome.Fail(
                    BackupFailureKind.CorruptArchive,
                    $"Could not extract bundle: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return BackupOutcome.Fail(
                    BackupFailureKind.WriteFailed,
                    $"Could not extract bundle: {ex.Message}");
            }

            List<SwapStep> plan;
            try
            {
                plan = BuildFullPlan(selected, extracted, final, mode);
            }
            catch (CategoryPrepareException ex)
            {
                return BackupOutcome.Fail(
                    BackupFailureKind.WriteFailed,
                    $"Import failed while preparing category '{ex.Category}': {ex.InnerException?.Message ?? ex.Message}. " +
                    "Nothing was written to the live tree.");
            }

            try
            {
                if (SimulateCommitPhaseFailureForTest is not null) throw SimulateCommitPhaseFailureForTest();
                CommitWithUndo(plan, undo, _log);
            }
            catch (ImportCommitException ex)
            {
                if (!ex.RollbackSucceeded)
                {
                    preserveStagingForManualRecovery = true;
                    _log(
                        $"[backup] rollback for category '{ex.Category}' did not fully succeed; " +
                        $"preserving staging directory '{staging}' for manual recovery.");

                    return BackupOutcome.Fail(
                        BackupFailureKind.WriteFailed,
                        $"Import failed while applying category '{ex.Category}', and the automatic rollback did " +
                        $"not fully restore every changed item: {ex.InnerException?.Message ?? ex.Message}. " +
                        $"The originals may still be recoverable from '{staging}' — restoring the pre-import " +
                        "snapshot is the safer path.");
                }

                return BackupOutcome.Fail(
                    BackupFailureKind.WriteFailed,
                    $"Import failed while applying category '{ex.Category}' and was rolled back: " +
                    $"{ex.InnerException?.Message ?? ex.Message}.");
            }
            catch (Exception)
            {
                // Anything that is not an ImportCommitException means CommitWithUndo's own
                // per-step catch did not recognize the failure - RollBack never ran, and
                // already-committed steps' originals (if any) sit only in undo/ under staging.
                // The unconditional cleanup below would destroy that last recovery path right
                // after this exception surfaces to the caller as "the operation failed" (I3).
                // Preserve staging and let the exception keep propagating; callers already treat
                // an escaped exception as failure, so nothing about their observable behavior
                // changes - only whether recovery is still possible afterward does.
                preserveStagingForManualRecovery = true;
                throw;
            }

            // Name the credential gap in the outcome itself. Bundles carry no secret material,
            // so imported SSH profiles look complete but cannot authenticate until the user
            // re-enters passwords — a silent partial failure if nothing says so. The Settings
            // page has copy for this; the CLI and any other caller only ever see this string.
            string credentialNote = selected.Contains(BackupCategory.Connections)
                ? " Connection passwords are not included in a bundle — re-enter them on first connect."
                : string.Empty;

            string categoryWord = selected.Length == 1 ? "category" : "categories";
            return BackupOutcome.Ok($"{operationNoun} {selected.Length} {categoryWord} ({mode}).{credentialNote}");
        }
        finally
        {
            if (!preserveStagingForManualRecovery)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { _log($"[backup] could not clean up import staging directory '{staging}': {ex.Message}"); }
            }
        }
    }

    /// <summary>One live path that Phase 2 will swap for computed/extracted content.</summary>
    /// <summary>
    /// One Phase-2 commit step. <paramref name="SourcePath"/> is null for an F3 removal step -
    /// "this category is selected, the bundle is authoritative for it (Replace mode), and the
    /// bundle simply omits this file" - in which case the live path is renamed aside by
    /// <see cref="CommitWithUndo"/> like any other step (so a later category's failure still rolls
    /// it back) but nothing is moved back into its place, leaving it removed.
    /// </summary>
    private sealed record SwapStep(BackupCategory Category, string LivePath, string? SourcePath, bool IsDirectory);

    /// <summary>Carries which category was being prepared when Phase 1 failed.</summary>
    private sealed class CategoryPrepareException(BackupCategory category, Exception inner)
        : Exception(inner.Message, inner)
    {
        public BackupCategory Category { get; } = category;
    }

    /// <summary>
    /// Carries which category was being committed when Phase 2 failed, and whether the
    /// automatic rollback that followed actually managed to restore everything it touched.
    /// </summary>
    private sealed class ImportCommitException(BackupCategory category, Exception inner, bool rollbackSucceeded)
        : Exception(inner.Message, inner)
    {
        public BackupCategory Category { get; } = category;
        public bool RollbackSucceeded { get; } = rollbackSucceeded;
    }

    /// <summary>Phase 1: compute every selected category's result under <paramref name="final"/>, touching nothing live.</summary>
    private List<SwapStep> BuildFullPlan(
        BackupCategory[] selected, string extracted, string final, ImportMode mode)
    {
        var plan = new List<SwapStep>();
        foreach (var category in selected)
        {
            try
            {
                plan.AddRange(BuildPlan(category, extracted, final, mode));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                throw new CategoryPrepareException(category, ex);
            }
        }

        return plan;
    }

    private List<SwapStep> BuildPlan(BackupCategory category, string extracted, string final, ImportMode mode)
    {
        var steps = new List<SwapStep>();

        switch (category)
        {
            case BackupCategory.Settings when mode == ImportMode.Merge:
                {
                    string livePath = Path.Combine(RootDirectory, "settings.json");
                    string? computed = MergeJsonObjectFile(
                        Path.Combine(extracted, "settings.json"),
                        livePath,
                        Path.Combine(final, "settings.json"));
                    if (computed is not null) steps.Add(new SwapStep(category, livePath, computed, IsDirectory: false));
                    break;
                }

            case BackupCategory.Connections when mode == ImportMode.Merge:
                {
                    string profilesLive = Path.Combine(RootDirectory, "ssh", "profiles.json");
                    string? profilesFinal = MergeProfilesFile(
                        Path.Combine(extracted, "ssh", "profiles.json"),
                        profilesLive,
                        Path.Combine(final, "ssh", "profiles.json"));
                    if (profilesFinal is not null) steps.Add(new SwapStep(category, profilesLive, profilesFinal, false));

                    string hostsLive = Path.Combine(RootDirectory, "ssh", "native_known_hosts.json");
                    string? hostsFinal = MergeJsonArrayFile(
                        Path.Combine(extracted, "ssh", "native_known_hosts.json"),
                        hostsLive,
                        Path.Combine(final, "ssh", "native_known_hosts.json"));
                    if (hostsFinal is not null) steps.Add(new SwapStep(category, hostsLive, hostsFinal, false));
                    break;
                }

            default:
                foreach (var entry in BackupCatalog.EntriesFor(category))
                {
                    string stagedPath = Path.Combine(extracted, entry.SourceRelativePath);
                    string livePath = Path.Combine(RootDirectory, entry.SourceRelativePath);

                    if (entry.IsDirectory)
                    {
                        string finalDir = Path.Combine(final, entry.SourceRelativePath);
                        string? computed = BuildDirectoryPlan(stagedPath, livePath, finalDir, mode);
                        if (computed is not null) steps.Add(new SwapStep(category, livePath, computed, IsDirectory: true));
                    }
                    else if (File.Exists(stagedPath))
                    {
                        // No merge needed — replace and the file-copy path (Snippets in either
                        // mode; Settings/Connections in Replace mode) both hand the raw extracted
                        // file straight to Phase 2, unmodified.
                        steps.Add(new SwapStep(category, livePath, stagedPath, IsDirectory: false));
                    }
                    else if (mode == ImportMode.Replace && File.Exists(livePath))
                    {
                        // F3: the file-entry analogue of BuildDirectoryPlan's Replace handling
                        // above. This category was selected and Replace means the bundle becomes
                        // the truth for it — a bundle that omits this particular catalog file
                        // (e.g. Connections carrying profiles.json but not
                        // native_known_hosts.json) must remove the live file, not leave it as a
                        // pre-import leftover. Routed through the same commit/undo journal as
                        // every other step (SourcePath: null means "removal" — see SwapStep and
                        // CommitWithUndo), so it rolls back exactly like any other step if a later
                        // category fails.
                        steps.Add(new SwapStep(category, livePath, SourcePath: null, IsDirectory: false));
                    }
                }
                break;
        }

        return steps;
    }

    /// <summary>
    /// Computes the final content of one catalog directory without touching the live one.
    /// Merge: local-only files survive, bundle files win per-name. Replace: the bundle becomes
    /// the truth for this directory, including when it donates nothing — an absent or empty
    /// staged directory still means "clear the live directory" (not "leave it alone").
    /// </summary>
    private static string? BuildDirectoryPlan(string stagedDirectory, string liveDirectory, string finalDirectory, ImportMode mode)
    {
        bool stagedHasContent = Directory.Exists(stagedDirectory) && Directory.EnumerateFileSystemEntries(stagedDirectory).Any();
        bool liveExists = Directory.Exists(liveDirectory);

        if (mode == ImportMode.Merge)
        {
            if (!stagedHasContent) return null; // nothing new to overlay; leave live untouched

            Directory.CreateDirectory(finalDirectory);
            // Skip ".bak" only on the staged (bundle) pass — a user's own local ".bak" sibling
            // is local-only content and must survive a Merge like any other local-only file.
            // Skipping it on the bundle side still stops a ".bak" the bundle carried (e.g.
            // exported from a machine where some other subsystem left one) from being carried
            // forward and compounding on a future export/import round trip.
            if (liveExists) CopyDirectoryContents(liveDirectory, finalDirectory, skipBakFiles: false);
            CopyDirectoryContents(stagedDirectory, finalDirectory, skipBakFiles: true); // bundle overwrites same-named files
            return finalDirectory;
        }

        if (!stagedHasContent && !liveExists) return null; // both empty: genuinely nothing to do
        if (stagedHasContent) return stagedDirectory; // move the extracted directory straight into place

        Directory.CreateDirectory(finalDirectory); // staged absent/empty but live has content: swap in empty
        return finalDirectory;
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory, bool skipBakFiles)
    {
        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (skipBakFiles && file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;

            string relative = Path.GetRelativePath(sourceDirectory, file);
            string destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Phase 2: commits every step with an undo journal. Each destination is renamed aside
    /// before the new content is moved into place, so a mid-commit failure rolls every
    /// already-committed step back — a "self-healing" import, not just "recoverable via a
    /// separate Restore of the pre-import snapshot".
    /// </summary>
    private static void CommitWithUndo(IReadOnlyList<SwapStep> plan, string undoRoot, Action<string> log)
    {
        Directory.CreateDirectory(undoRoot);
        var journal = new List<(string LivePath, string UndoPath, bool IsDirectory, bool HadOriginal)>();
        int counter = 0;

        foreach (var step in plan)
        {
            string undoPath = Path.Combine(undoRoot, (counter++).ToString(CultureInfo.InvariantCulture));

            try
            {
                bool hadOriginal = step.IsDirectory ? Directory.Exists(step.LivePath) : File.Exists(step.LivePath);

                if (hadOriginal)
                {
                    if (step.IsDirectory) Directory.Move(step.LivePath, undoPath);
                    else File.Move(step.LivePath, undoPath);
                }

                // Recorded before the risky move below, so a failure on THIS step still rolls
                // its own rename-aside back, not just the steps that came before it.
                journal.Add((step.LivePath, undoPath, step.IsDirectory, hadOriginal));

                // F3: a null SourcePath is a removal step - the rename-aside above already
                // cleared step.LivePath (recorded in the journal for rollback); there is nothing
                // to move back into its place.
                if (step.SourcePath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(step.LivePath)!);
                    if (step.IsDirectory) Directory.Move(step.SourcePath, step.LivePath);
                    else File.Move(step.SourcePath, step.LivePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                bool rollbackSucceeded = RollBack(journal, log);
                throw new ImportCommitException(step.Category, ex, rollbackSucceeded);
            }
        }
    }

    /// <summary>
    /// Undoes every journaled step, in reverse. Returns false if any step could not be fully
    /// restored. Internal (rather than private) so a test can pin this return value directly: a
    /// hand-built journal entry whose undo path does not exist makes the per-entry
    /// <see cref="File.Move(string, string)"/> throw <see cref="FileNotFoundException"/>,
    /// swallowed here into a false return - deterministic on every OS, unlike trying to provoke
    /// the same failure through <see cref="CommitWithUndo"/>'s own real file operations.
    /// </summary>
    internal static bool RollBack(
        List<(string LivePath, string UndoPath, bool IsDirectory, bool HadOriginal)> journal, Action<string> log)
    {
        bool allSucceeded = true;

        for (int i = journal.Count - 1; i >= 0; i--)
        {
            var (livePath, undoPath, isDirectory, hadOriginal) = journal[i];
            try
            {
                // Remove whatever the forward move managed to place at livePath before this (or
                // a later) step failed.
                if (isDirectory) { if (Directory.Exists(livePath)) Directory.Delete(livePath, recursive: true); }
                else { if (File.Exists(livePath)) File.Delete(livePath); }

                if (hadOriginal)
                {
                    if (isDirectory) Directory.Move(undoPath, livePath);
                    else File.Move(undoPath, livePath);
                }
            }
            catch (Exception ex)
            {
                allSucceeded = false;
                log($"[backup] rollback could not restore '{livePath}' from '{undoPath}': {ex.Message}");
            }
        }

        return allSucceeded;
    }

    /// <summary>
    /// Key-by-key merge of two JSON objects, bundle winning per key, computed into
    /// <paramref name="finalPath"/> — never live. Operates on <see cref="JsonNode"/> rather than
    /// a typed model so unknown and future keys survive and settings.json keeps its PascalCase
    /// names. Malformed local content is treated as absent so the bundle wins wholesale; a
    /// malformed staged (bundle) file is a genuine bundle defect and throws, aborting Phase 1
    /// before any live write.
    /// </summary>
    private static string? MergeJsonObjectFile(string stagedPath, string livePath, string finalPath)
    {
        if (!File.Exists(stagedPath)) return null;

        var incoming = ParseObjectOrThrow(stagedPath);

        var existing = TryParseObject(livePath);

        JsonObject merged;
        if (existing is not null)
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

        WriteJson(finalPath, merged);
        return finalPath;
    }

    /// <summary>
    /// Merges <c>profiles.json</c>'s profile array by <c>Id</c>, bundle winning on conflict,
    /// computed into <paramref name="finalPath"/> — never live. The real file on disk is
    /// PascalCase (<c>SchemaVersion</c>/<c>Profiles</c> — <c>JsonSshProfileStore</c>'s
    /// <c>SshJsonContext</c> sets no naming policy), so the profiles array is located
    /// case-insensitively on both sides and written back through whichever key text was actually
    /// found — never a hardcoded literal — so a legacy-cased document is normalized in place
    /// rather than gaining a second, duplicate key.
    /// </summary>
    private static string? MergeProfilesFile(string stagedPath, string livePath, string finalPath)
    {
        if (!File.Exists(stagedPath)) return null;

        var incoming = ParseObjectOrThrow(stagedPath);

        var existing = TryParseObject(livePath);
        if (existing is null)
        {
            WriteJson(finalPath, incoming);
            return finalPath;
        }

        string key = FindKeyOrDefault(existing, "profiles", "Profiles");

        var byId = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        // AbsorbProfiles takes byId/order as parameters rather than capturing them in a local
        // function. Both forms are behaviourally identical, but static analysis does not track
        // mutation of captured locals across a local-function call, so the closure version made
        // the loop below look like it iterated a provably empty list (SonarCloud S4158, a false
        // positive). Passing them explicitly keeps the analyser honest without a suppression.
        AbsorbProfiles(existing, byId, order);
        AbsorbProfiles(incoming, byId, order); // bundle wins: absorbed second

        var merged = new JsonArray();
        foreach (string id in order) merged.Add(byId[id]);

        // key is the real key text FindKeyOrDefault located (or "Profiles" for a brand-new
        // document), so a plain indexer assignment both updates and preserves the property's
        // original position — unlike Remove-then-Add, which would move it to the end.
        existing[key] = merged;

        WriteJson(finalPath, existing);
        return finalPath;
    }

    /// <summary>
    /// Folds one document's profile array into <paramref name="byId"/>, recording first-seen
    /// order in <paramref name="order"/>. Called for the live document first and the bundle's
    /// second, so a bundle profile overwrites a local one sharing its <c>Id</c> while local-only
    /// profiles survive and the original ordering is preserved.
    /// </summary>
    private static void AbsorbProfiles(
        JsonObject document,
        Dictionary<string, JsonNode> byId,
        List<string> order)
    {
        var array = GetPropertyArray(document, "profiles");
        if (array is null) return;

        foreach (var element in array)
        {
            string? id = element?["Id"]?.GetValue<string>();
            if (element is null || string.IsNullOrWhiteSpace(id)) continue;
            if (!byId.ContainsKey(id)) order.Add(id);
            byId[id] = element.DeepClone();
        }
    }

    /// <summary>
    /// Union of two JSON arrays, deduped by each element's serialized form, computed into
    /// <paramref name="finalPath"/> — never live.
    /// </summary>
    private static string? MergeJsonArrayFile(string stagedPath, string livePath, string finalPath)
    {
        if (!File.Exists(stagedPath)) return null;

        var incoming = ParseArrayOrThrow(stagedPath);

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

        var existing = TryParseArray(livePath);
        if (existing is not null) Absorb(existing);
        Absorb(incoming);

        WriteJson(finalPath, merged);
        return finalPath;
    }

    /// <summary>Finds a property case-insensitively, returning its exact key text, or <paramref name="fallback"/> if absent.</summary>
    private static string FindKeyOrDefault(JsonObject document, string caseInsensitiveName, string fallback)
    {
        foreach (var pair in document)
        {
            if (string.Equals(pair.Key, caseInsensitiveName, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        }

        return fallback;
    }

    private static JsonArray? GetPropertyArray(JsonObject document, string caseInsensitiveName)
    {
        foreach (var pair in document)
        {
            if (string.Equals(pair.Key, caseInsensitiveName, StringComparison.OrdinalIgnoreCase)) return pair.Value as JsonArray;
        }

        return null;
    }

    /// <summary>
    /// Bundle content must be well-formed. Lets a parse failure propagate so Phase 1 aborts before
    /// any live write (I4). F4: a syntactically valid JSON document whose root is the wrong KIND
    /// (e.g. an array where an object is required) is not a parse failure - <c>JsonNode.Parse</c>
    /// succeeds and the failed <c>as JsonObject</c> cast used to just return null, which every
    /// caller treated identically to "nothing to merge, leave live untouched". That silently
    /// skipped the whole category instead of aborting the import, so this throws
    /// <see cref="InvalidDataException"/> instead - caught alongside every other Phase-1
    /// preparation failure in <see cref="BuildFullPlan"/> and surfaced as a typed
    /// <see cref="BackupOutcome"/> failure, not an escaped exception.
    /// </summary>
    private static JsonObject ParseObjectOrThrow(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        return node as JsonObject
            ?? throw new InvalidDataException(
                $"'{path}' must contain a JSON object at its root, but found {DescribeJsonRootKind(node)}.");
    }

    /// <summary>Bundle content must be well-formed. Lets a parse failure propagate so Phase 1 aborts before any live write (I4). See <see cref="ParseObjectOrThrow"/>'s remarks on the F4 wrong-root-kind case this also covers.</summary>
    private static JsonArray ParseArrayOrThrow(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        return node as JsonArray
            ?? throw new InvalidDataException(
                $"'{path}' must contain a JSON array at its root, but found {DescribeJsonRootKind(node)}.");
    }

    private static string DescribeJsonRootKind(JsonNode? node) => node switch
    {
        null => "a JSON null",
        JsonArray => "a JSON array",
        JsonObject => "a JSON object",
        _ => "a JSON scalar value",
    };

    /// <summary>Malformed or absent LOCAL content must not abort the import — treated as absent so the bundle wins wholesale (I4).</summary>
    private static JsonObject? TryParseObject(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Malformed or absent LOCAL content must not abort the import — treated as absent so the bundle wins wholesale (I4).</summary>
    private static JsonArray? TryParseArray(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonArray; }
        catch (JsonException) { return null; }
    }

    private static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(IndentedJson));
    }

    /// <summary>Snapshots kept regardless of age.</summary>
    public const int MaxSnapshots = 20;

    /// <summary>Snapshots newer than this are kept regardless of count.</summary>
    public static readonly TimeSpan SnapshotRetentionWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Millisecond precision (not just whole seconds), so two forced snapshots of identical
    /// content taken close together (e.g. a PreImport immediately followed by a PreRestore) get
    /// distinct ids rather than colliding on one file name (M6) — a collision that would
    /// otherwise silently disappear the first snapshot, since <see cref="Export"/> writes via
    /// <see cref="File.Move(string, string, bool)"/> with <c>overwrite: true</c>.
    /// </summary>
    private const string SnapshotTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>
    /// Hex chars of the content hash kept in the snapshot id and compared for dedupe. 64 bits
    /// (16 hex chars) rather than 32 (8 hex chars): the dedupe check is a single-pair comparison
    /// per <see cref="Snapshot"/> call, and a false-positive collision there means silently
    /// skipping a snapshot of a genuinely changed configuration - no error, no signal, and the
    /// user later finds no rollback point for that change. 64 bits puts that probability at
    /// roughly 1 in 2^64, which is negligible; 32 bits is not.
    /// </summary>
    private const int SnapshotHashPrefixLength = 16;

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
            // Only the first SnapshotHashPrefixLength hex chars survive on disk (they're
            // embedded in the file name), so that's the granularity ListSnapshots can ever
            // recover. Comparing against the full 64-char hash here would compare unequal
            // length strings and never match, silently disabling dedupe - so the comparison
            // (and the stored ContentHash) must use the same truncated form that
            // TryParseSnapshot parses back out of the id.
            string hashPrefix = ComputeContentHash()[..SnapshotHashPrefixLength];

            if (reason == SnapshotReason.Auto)
            {
                var newest = ListSnapshots().FirstOrDefault();
                if (newest is not null && string.Equals(newest.ContentHash, hashPrefix, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            var now = _timeProvider.GetUtcNow();
            string id = $"{ReasonToken(reason)}-{now.UtcDateTime.ToString(SnapshotTimestampFormat, CultureInfo.InvariantCulture)}-{hashPrefix}";
            string path = Path.Combine(BackupsDirectory, id + BundleExtension);

            Directory.CreateDirectory(BackupsDirectory);

            // F2: ExportCore, not Export - this write's destination is inside BackupsDirectory,
            // one of the paths the public Export's guard rejects for an arbitrary caller-supplied
            // destination. See Export's own remarks on why this internal call is exempt.
            var outcome = ExportCore(path, categories: null);
            if (!outcome.Success)
            {
                _log($"[backup] snapshot failed: {outcome.Message}");
                return null;
            }

            // A snapshot exists on disk at this point - Export already wrote it durably. A
            // pruning failure (e.g. Directory.GetFiles throwing on BackupsDirectory) must not
            // make Snapshot() report null, which callers would read as "nothing was written."
            // So pruning gets its own try/catch, isolated from the outer one that guards the
            // write itself.
            try
            {
                PruneSnapshots();
            }
            catch (Exception ex)
            {
                _log($"[backup] snapshot pruning failed: {ex.Message}");
            }

            return new SnapshotInfo(id, reason, now, new FileInfo(path).Length, hashPrefix, path);
        }
        catch (Exception ex)
        {
            _log($"[backup] snapshot failed: {ex.Message}");
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

                // Sort on the forward-slash-normalized relative path that actually gets hashed,
                // not the raw OS path: '\' (0x5C) and '/' (0x2F) sit at different points in
                // ASCII, so a directory and a sibling file whose names diverge in that range
                // could sort differently on Windows vs Linux if we ordered by the native path,
                // hashing an identical tree to different digests across platforms.
                var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                    .Select(file => (File: file, Relative: Path.GetRelativePath(source, file).Replace('\\', '/')))
                    .OrderBy(f => f.Relative, StringComparer.Ordinal);

                foreach (var (file, relative) in files)
                {
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
            catch (Exception ex) { _log($"[backup] could not prune {snapshot.Id}: {ex.Message}"); }
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

    /// <summary>Parses <c>&lt;reason&gt;-&lt;timestamp&gt;-&lt;hash16&gt;</c>. The reason itself may contain a dash.</summary>
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

    /// <summary>
    /// F2: true when <paramref name="destinationPath"/> resolves onto, or underneath, a path
    /// <see cref="BackupCatalog.Entries"/> actually backs up, or underneath
    /// <see cref="BackupsDirectory"/> itself. <paramref name="reason"/> names which.
    ///
    /// Compares fully-resolved paths so a relative destination, a trailing separator, or a
    /// <c>..</c> segment can't slip past - the same OS-appropriate comparer
    /// <see cref="BackupCatalog"/> already uses (case-insensitive on Windows, where the live paths
    /// this guards actually run, case-sensitive elsewhere).
    ///
    /// <see cref="BackupsDirectory"/> is rejected too, even though it is not itself a
    /// <see cref="BackupCatalog.Entries"/> source: it is where <see cref="Snapshot"/> writes the
    /// pre-import/pre-restore rollback points <see cref="Restore"/> depends on and
    /// <see cref="ListSnapshots"/>/pruning manage by file name. An export that happened to land on
    /// an existing snapshot's exact file name (matching by GUID or timestamp is unlikely by
    /// accident, but the CLI and MCP tool both accept an arbitrary path, so not impossible) would
    /// silently destroy that rollback point the same way an aliased catalog entry destroys live
    /// configuration - and pruning already depends on nothing but this service writing into that
    /// directory, an invariant an arbitrary Export destination would break either way.
    /// </summary>
    private bool TryDescribeProtectedDestination(string destinationPath, out string? reason)
    {
        string comparableDestination;
        try
        {
            // Fix round 2 (Codex review): Path.GetFullPath(destinationPath) - NOT
            // Path.GetFullPath(destinationPath, RootDirectory). A relative destinationPath must
            // resolve against the process's current working directory, exactly like
            // BundleWriter.Write's own FileStream/File.Move do with the raw string it is handed -
            // resolving it against RootDirectory here instead let a relative path bypass this
            // guard while still landing on a live catalog file: with CWD =
            // "<appdata>/ssh", "backup export profiles.json" resolved here to
            // "<appdata>/profiles.json" (not a catalog entry - allowed) but was actually written
            // to "<appdata>/ssh/profiles.json" (the live Connections file) by BundleWriter, which
            // never heard of RootDirectory at all. No base directory means both agree.
            comparableDestination = Path.GetFullPath(destinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // An invalid or unnormalizable path is BundleWriter's failure to report (it hits the
            // same GetFullPath/FileStream normalization and already has a typed WriteFailed path
            // for exactly this) - this guard only cares about a path that resolved successfully
            // onto something live. Widened past ArgumentException (fix round 2) to match every
            // exception GetFullPath's own documentation lists for a malformed path, so one of
            // these doesn't escape Export unhandled the way ArgumentException-only did before.
            reason = null;
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var entry in BackupCatalog.Entries)
        {
            string source = Path.GetFullPath(BackupCatalog.ResolveSource(RootDirectory, entry));
            if (IsSameOrUnder(comparableDestination, source, comparison))
            {
                reason = $"it is the live '{entry.SourceRelativePath}' NovaTerminal backs up (or a path under it); " +
                    "exporting there would overwrite it with the bundle before the export finished writing.";
                return true;
            }
        }

        string backupsDirectory = Path.GetFullPath(BackupsDirectory);
        if (IsSameOrUnder(comparableDestination, backupsDirectory, comparison))
        {
            reason = "it is inside the backups directory NovaTerminal manages for snapshots; " +
                "exporting there could silently overwrite an existing snapshot.";
            return true;
        }

        reason = null;
        return false;
    }

    private static bool IsSameOrUnder(string candidate, string ancestor, StringComparison comparison)
    {
        string candidateNormalized = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string ancestorNormalized = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(candidateNormalized, ancestorNormalized, comparison)
            || candidateNormalized.StartsWith(ancestorNormalized + Path.DirectorySeparatorChar, comparison);
    }

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
