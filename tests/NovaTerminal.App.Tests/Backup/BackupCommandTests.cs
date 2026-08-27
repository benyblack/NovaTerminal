using NovaTerminal.Backup;
using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupCommandTests
{
    [Fact]
    public void IsSupportedCliMode_RecognizesBackupVerb()
    {
        Assert.True(BackupCommand.IsSupportedCliMode(new[] { "backup", "list" }));
        Assert.False(BackupCommand.IsSupportedCliMode(new[] { "replay", "list" }));
        Assert.False(BackupCommand.IsSupportedCliMode(Array.Empty<string>()));
    }

    [Fact]
    public void Export_WritesBundleAndReportsPath()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        var (code, stdout, _) = Run(tree, "backup", "export", bundle);

        Assert.Equal(0, code);
        Assert.True(File.Exists(bundle));
        Assert.Contains("cli.novabackup", stdout);
    }

    [Fact]
    public void List_PrintsIdReasonAndSize()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var snapshot = new BackupService(tree.Root).Snapshot(SnapshotReason.Auto);

        var (code, stdout, _) = Run(tree, "backup", "list");

        Assert.Equal(0, code);
        Assert.Contains(snapshot!.Id, stdout);
        Assert.Contains("auto", stdout);
    }

    [Fact]
    public void List_WithNoSnapshots_SucceedsWithMessage()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, stdout, _) = Run(tree, "backup", "list");

        Assert.Equal(0, code);
        Assert.Contains("No snapshots", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_RequiresAModeFlag()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        new BackupService(tree.Root).Export(bundle);

        var (code, _, stderr) = Run(tree, "backup", "import", bundle);

        Assert.Equal(2, code);
        Assert.Contains("--merge", stderr);
        Assert.Contains("--replace", stderr);
    }

    [Fact]
    public void Import_RejectsBothModeFlags()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        new BackupService(tree.Root).Export(bundle);

        var (code, _, stderr) = Run(tree, "backup", "import", bundle, "--merge", "--replace");

        Assert.Equal(2, code);
        Assert.Contains("mutually exclusive", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_WithReplace_Succeeds()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":33}""");
        string bundle = Path.Combine(source.Root, "cli.novabackup");
        new BackupService(source.Root).Export(bundle);

        using var target = BackupTestTree.CreatePopulated();
        var (code, stdout, stderr) = Run(target, "backup", "import", bundle, "--replace");

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        Assert.Contains("33", target.ReadFile("settings.json"));

        // BackupTestTree.CreatePopulated() writes ssh/profiles.json, so Connections is among the
        // imported categories and BackupService.ImportCore appends its credentials warning to the
        // outcome message. The CLI is the only place a user ever sees that message - a future
        // refactor that swaps it for a bare "Imported successfully." must fail this test, not
        // silently drop the only notice the user gets that passwords were not carried over.
        Assert.Contains("Connection passwords are not included in a bundle", stdout);
    }

    [Fact]
    public void Restore_UnknownId_ReturnsOne()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup", "restore", "auto-19700101T000000Z-deadbeef");

        Assert.Equal(1, code);
        Assert.Contains("deadbeef", stderr);
    }

    [Fact]
    public void Restore_KnownId_Succeeds_AndRollsBackTrackedFile()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        // Change a tracked file after the snapshot, so a successful restore is actually
        // observable on disk rather than just a green exit code.
        tree.WriteFile("settings.json", """{"FontSize":99}""");

        var (code, stdout, stderr) = Run(tree, "backup", "restore", snapshot!.Id);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
        string settingsAfterRestore = tree.ReadFile("settings.json");
        Assert.DoesNotContain("99", settingsAfterRestore);
        Assert.Contains("14", settingsAfterRestore); // original FontSize from CreatePopulated()
    }

    [Fact]
    public void Import_FlagInBundlePathPosition_ReturnsUsageErrorNotFileNotFound()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(tree.Root, "cli.novabackup");
        new BackupService(tree.Root).Export(bundle);

        // args[2] is "--merge" instead of a bundle path - the real path landed one slot too
        // late. Scanning the whole array for mode flags would have picked up args[2] itself and
        // proceeded to a confusing "file not found" against a path literally named "--merge".
        var (code, _, stderr) = Run(tree, "backup", "import", "--merge", bundle);

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could not", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_BundlePathEqualToModeFlag_DoesNotSelfSelectMode()
    {
        using var tree = BackupTestTree.CreatePopulated();

        // Only three tokens: "--replace" sits in the bundle-path position (args[2]) with no mode
        // flag after it. Scanning the whole array for "--replace" would find this one and
        // silently treat it as the requested mode instead of failing with "specify --merge or
        // --replace" - self-selecting a mode nobody asked for.
        var (code, _, stderr) = Run(tree, "backup", "import", "--replace");

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoSubcommand_ReturnsUsageError()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup");

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_WithNoPath_ReturnsUsageError()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup", "export");

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_WithNoId_ReturnsUsageError()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup", "restore");

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSubcommand_ReturnsUsageError()
    {
        using var tree = BackupTestTree.CreatePopulated();

        var (code, _, stderr) = Run(tree, "backup", "frobnicate");

        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Code, string Stdout, string Stderr) Run(BackupTestTree tree, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = BackupCommand.Execute(args, stdout, stderr, tree.Root);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
