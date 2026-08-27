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
