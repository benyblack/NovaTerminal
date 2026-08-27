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
        var (code, _, stderr) = Run(target, "backup", "import", bundle, "--replace");

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        Assert.Contains("33", target.ReadFile("settings.json"));
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
