using NovaTerminal.Backup;
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
            // File.Exists alone can't tell a real bundle from a truncated/corrupt one that
            // happens to land on disk; open it the way a consumer would.
            Assert.True(BundleReader.Open(destination).Success);
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
    public void BackupExport_RejectsRelativeDestinationPath()
    {
        string root = CreateTree();
        try
        {
            string result = BackupTools.BackupExport("relative-name.novabackup", root);

            Assert.Contains("absolute", result, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "relative-name.novabackup")));
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

    [Fact]
    public void BackupList_WithNoRootDirectory_UsesAppDataRootOverride()
    {
        string root = CreateTree();
        string? originalOverride = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        try
        {
            // Seed a real snapshot through BackupService directly, so the on-disk file name
            // format (reason-timestamp-hash.novabackup) is whatever the real writer produces,
            // not a hand-guessed literal that could drift from it.
            var seeded = new BackupService(root).Snapshot(SnapshotReason.PreImport);
            Assert.NotNull(seeded);

            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", root);

            string result = BackupTools.BackupList();

            Assert.Contains(seeded!.Id, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", originalOverride);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BackupList_WithEmptyRootDirectory_BehavesLikeOmitted()
    {
        string root = CreateTree();
        string? originalOverride = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", root);

            string result = BackupTools.BackupList(rootDirectory: "");

            Assert.Contains("No snapshots", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", originalOverride);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BackupExport_WithEmptyRootDirectory_BehavesLikeOmitted()
    {
        string root = CreateTree();
        string? originalOverride = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", root);
            string destination = Path.Combine(root, "empty-root-export.novabackup");

            string result = BackupTools.BackupExport(destination, rootDirectory: "");

            Assert.True(File.Exists(destination));
            Assert.Contains(destination, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", originalOverride);
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
