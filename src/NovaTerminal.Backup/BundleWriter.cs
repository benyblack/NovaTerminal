using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NovaTerminal.Backup;

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
