using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NovaTerminal.Backup;

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

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="rootOverride">Test seam. Null uses <see cref="AppPaths.RootDirectory"/>.</param>
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length < 2)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        var service = new BackupService(rootOverride ?? AppPaths.RootDirectory, log: AppLogger.Log);

        return args[1].ToLowerInvariant() switch
        {
            "export" => Export(args, stdout, stderr, service),
            "import" => Import(args, stdout, stderr, service),
            "list" => List(stdout, stderr, service),
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

        string bundlePath = args[2];

        // The bundle path is strictly positional (args[2]) - mode flags are only ever read from
        // what follows it. Without this guard, `backup import --merge somebundle.novabackup`
        // would silently take "--merge" itself as the bundle path (then fail with a confusing
        // file-not-found once service.Import tries to open it), and a real bundle path that
        // happened to equal "--merge"/"--replace" would self-select that mode even though it was
        // never meant as a flag. Rejecting a flag-shaped token in the path position up front
        // turns both into one clear usage error instead.
        if (IsModeFlag(bundlePath))
        {
            return Fail(stderr, $"Expected a bundle path before {bundlePath}.\n" + Usage);
        }

        string[] flags = args[3..];
        bool merge = flags.Any(a => string.Equals(a, "--merge", StringComparison.OrdinalIgnoreCase));
        bool replace = flags.Any(a => string.Equals(a, "--replace", StringComparison.OrdinalIgnoreCase));

        if (merge && replace)
        {
            return Fail(stderr, "--merge and --replace are mutually exclusive.\n" + Usage);
        }

        if (!merge && !replace)
        {
            // No default: guessing wrong here overwrites the user's configuration.
            return Fail(stderr, "Specify --merge or --replace.\n" + Usage);
        }

        var outcome = service.Import(bundlePath, merge ? ImportMode.Merge : ImportMode.Replace);
        if (!outcome.Success)
        {
            stderr.WriteLine(outcome.Message);
            return 1;
        }

        // Print the service's own outcome message verbatim rather than composing a fresh one:
        // when Connections is among the imported categories, that message is the only place the
        // "connection passwords are not included in a bundle" warning reaches the user, since
        // bundles never carry password material.
        stdout.WriteLine(outcome.Message);
        return 0;
    }

    private static bool IsModeFlag(string value) =>
        string.Equals(value, "--merge", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "--replace", StringComparison.OrdinalIgnoreCase);

    private static int List(TextWriter stdout, TextWriter stderr, BackupService service)
    {
        IReadOnlyList<SnapshotInfo> snapshots;
        try
        {
            snapshots = service.ListSnapshots();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unlike Snapshot(), ListSnapshots() carries no "never throws" contract - it walks
            // BackupsDirectory directly and a permissions problem or a directory that vanishes
            // mid-enumeration surfaces as a real exception. Without this guard that would escape
            // Execute entirely and break the documented 0/1/2 contract every other subcommand
            // honors.
            stderr.WriteLine($"Could not list snapshots: {ex.Message}");
            return 1;
        }

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
