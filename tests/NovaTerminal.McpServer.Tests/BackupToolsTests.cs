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
