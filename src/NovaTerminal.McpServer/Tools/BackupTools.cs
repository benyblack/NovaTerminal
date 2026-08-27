using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using NovaTerminal.Backup;

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
