using Ntilde.Backup;
using Ntilde.McpServer.Tools;

namespace Ntilde.McpServer.Tests;

public sealed class BackupToolsTests
{
    // M7: BackupList_WithNoRootDirectory_UsesAppDataRootOverride,
    // BackupList_WithEmptyRootDirectory_BehavesLikeOmitted, and
    // BackupExport_WithEmptyRootDirectory_BehavesLikeOmitted all mutate the process-global
    // NTILDE_APPDATA_ROOT environment variable around a get/set/restore that is not itself
    // atomic. xunit.v3 does not guarantee serial execution of test methods within one class, so
    // two of these interleaving their set/reset of the same env var is a genuine, CI-only race
    // (a local single-threaded run would never surface it) - one test's "restore to null/original"
    // landing between another's "set" and its own read would leak into that other test's
    // assertions. A private lock around just these three bodies is simpler than introducing a
    // whole extra collection-definition type for a single file.
    private static readonly object EnvVarGate = new();

    [Fact]
    public void BackupExport_WritesBundleAndReportsIt()
    {
        string root = CreateTree();
        try
        {
            string destination = Path.Combine(root, "agent-export.ntildebackup");

            string result = BackupTools.BackupExport(destination, root);

            Assert.True(File.Exists(destination));
            Assert.Contains("agent-export.ntildebackup", result);
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
            string blocked = Path.Combine(root, "blocked.ntildebackup");
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
            string result = BackupTools.BackupExport("relative-name.ntildebackup", root);

            Assert.Contains("absolute", result, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "relative-name.ntildebackup")));
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
        lock (EnvVarGate)
        {
            string root = CreateTree();
            string? originalOverride = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
            try
            {
                // Seed a real snapshot through BackupService directly, so the on-disk file name
                // format (reason-timestamp-hash.ntildebackup) is whatever the real writer produces,
                // not a hand-guessed literal that could drift from it.
                var seeded = new BackupService(root).Snapshot(SnapshotReason.PreImport);
                Assert.NotNull(seeded);

                Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);

                string result = BackupTools.BackupList();

                Assert.Contains(seeded!.Id, result);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", originalOverride);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BackupList_WithEmptyRootDirectory_BehavesLikeOmitted()
    {
        lock (EnvVarGate)
        {
            string root = CreateTree();
            string? originalOverride = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);

                string result = BackupTools.BackupList(rootDirectory: "");

                Assert.Contains("No snapshots", result, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", originalOverride);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BackupExport_WithEmptyRootDirectory_BehavesLikeOmitted()
    {
        lock (EnvVarGate)
        {
            string root = CreateTree();
            string? originalOverride = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);
                string destination = Path.Combine(root, "empty-root-export.ntildebackup");

                string result = BackupTools.BackupExport(destination, rootDirectory: "");

                Assert.True(File.Exists(destination));
                Assert.Contains(destination, result);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", originalOverride);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ntilde_mcp_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), """{"FontSize":14}""");
        return root;
    }
}
