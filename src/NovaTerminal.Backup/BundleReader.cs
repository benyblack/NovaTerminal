using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace NovaTerminal.Backup;

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
                // A bare StartsWith on the full paths is bypassable — "D:\tmp\xevil" starts with
                // "D:\tmp\x" as a string even though it is a sibling, not a descendant. Comparing
                // the relative path's shape (does it climb out with "..", or land on another
                // root entirely) is immune to that. Path.GetRelativePath already resolves ".."
                // segments and already picks the platform-appropriate comparer (case-insensitive
                // on Windows, case-sensitive elsewhere) — the same rule BackupCatalog.IsSameOrUnder
                // follows for the same reason.
                string fullDestination = Path.GetFullPath(destination);
                string fullRoot = Path.GetFullPath(destinationRoot);
                string relativeToRoot = Path.GetRelativePath(fullRoot, fullDestination);
                if (relativeToRoot == ".."
                    || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathRooted(relativeToRoot))
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
            if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && !entry.FullName.EndsWith('/'))
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
