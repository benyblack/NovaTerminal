using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return BackupOutcome.Fail(
                BackupFailureKind.WriteFailed,
                "Destination path must not be empty.");
        }

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

    /// <summary>Snapshots kept regardless of age.</summary>
    public const int MaxSnapshots = 20;

    /// <summary>Snapshots newer than this are kept regardless of count.</summary>
    public static readonly TimeSpan SnapshotRetentionWindow = TimeSpan.FromDays(7);

    private const string SnapshotTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

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

            var outcome = Export(path);
            if (!outcome.Success)
            {
                AppLogger.Log($"[backup] snapshot failed: {outcome.Message}");
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
                AppLogger.Log($"[backup] snapshot pruning failed: {ex.Message}");
            }

            return new SnapshotInfo(id, reason, now, new FileInfo(path).Length, hashPrefix, path);
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
