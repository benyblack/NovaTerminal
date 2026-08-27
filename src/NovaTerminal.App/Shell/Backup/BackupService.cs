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
