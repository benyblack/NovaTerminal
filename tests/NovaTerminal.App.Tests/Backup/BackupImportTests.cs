using System.Text.Json;
using NovaTerminal.Shell.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupImportTests
{
    [Fact]
    public void Import_IntoEmptyTree_ReproducesSource()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreateEmpty();
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(source.ReadFile("settings.json"), target.ReadFile("settings.json"));
        Assert.Equal(
            source.ReadFile(Path.Combine("themes", "solarized.json")),
            target.ReadFile(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Merge_Settings_BundleWinsPerKey_LocalOnlyKeysSurvive()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":20,"ThemeName":"Solarized"}""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"CursorBlink":false}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        using var merged = target.ReadJson("settings.json");
        Assert.Equal(20, merged.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("Solarized", merged.RootElement.GetProperty("ThemeName").GetString());
        Assert.False(merged.RootElement.GetProperty("CursorBlink").GetBoolean());
    }

    [Fact]
    public void Replace_Settings_DropsLocalOnlyKeys()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":20}""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"CursorBlink":false}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        using var replaced = target.ReadJson("settings.json");
        Assert.Equal(20, replaced.RootElement.GetProperty("FontSize").GetInt32());
        Assert.False(replaced.RootElement.TryGetProperty("CursorBlink", out _));
    }

    [Fact]
    public void Merge_Themes_KeepsLocalOnlyTheme()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        Assert.True(target.Exists(Path.Combine("themes", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Replace_Themes_DropsLocalOnlyTheme()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.False(target.Exists(Path.Combine("themes", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Merge_Connections_MatchesProfilesById()
    {
        string sharedId = "11111111-1111-1111-1111-111111111111";
        string bundleOnlyId = "22222222-2222-2222-2222-222222222222";
        string localOnlyId = "33333333-3333-3333-3333-333333333333";

        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"schemaVersion":1,"profiles":[
              {"Id":"{{sharedId}}","Name":"From Bundle","Host":"bundle.example"},
              {"Id":"{{bundleOnlyId}}","Name":"Bundle Only","Host":"only.example"}]}
            """);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"schemaVersion":1,"profiles":[
              {"Id":"{{sharedId}}","Name":"Local Version","Host":"local.example"},
              {"Id":"{{localOnlyId}}","Name":"Local Only","Host":"keep.example"}]}
            """);

        string bundle = ExportFrom(source);
        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        using var doc = target.ReadJson(Path.Combine("ssh", "profiles.json"));
        var profiles = doc.RootElement.GetProperty("profiles").EnumerateArray().ToArray();

        Assert.Equal(3, profiles.Length);
        Assert.Equal(
            "From Bundle",
            profiles.Single(p => p.GetProperty("Id").GetString() == sharedId).GetProperty("Name").GetString());
        Assert.Contains(profiles, p => p.GetProperty("Id").GetString() == localOnlyId);
        Assert.Contains(profiles, p => p.GetProperty("Id").GetString() == bundleOnlyId);
    }

    [Fact]
    public void Replace_Connections_DropsLocalOnlyProfiles()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "profiles.json"), """
            {"schemaVersion":1,"profiles":[{"Id":"22222222-2222-2222-2222-222222222222","Name":"Bundle Only"}]}
            """);
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "profiles.json"), """
            {"schemaVersion":1,"profiles":[{"Id":"33333333-3333-3333-3333-333333333333","Name":"Local Only"}]}
            """);
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        using var doc = target.ReadJson(Path.Combine("ssh", "profiles.json"));
        var profiles = doc.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Single(profiles);
        Assert.Equal("Bundle Only", profiles[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void Import_LeavesCategoriesAbsentFromBundleUntouched()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("policy", "workspace_policy.json"), """{"local":true}""");

        string bundle = Path.Combine(source.Root, "themes-only.novabackup");
        new BackupService(source.Root, Clock()).Export(bundle, new[] { BackupCategory.Themes });

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.Equal("""{"local":true}""", target.ReadFile(Path.Combine("policy", "workspace_policy.json")));
    }

    /// <summary>
    /// A bundle carries no secret material, so an imported SSH profile looks complete but
    /// cannot authenticate. Every caller that only sees the outcome string — the CLI above all —
    /// must be told, or the failure is silent.
    /// </summary>
    [Fact]
    public void Import_WithConnections_OutcomeMentionsMissingPasswords()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains("passwords", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_WithoutConnections_OutcomeOmitsPasswordNote()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(source.Root, "themes-only.novabackup");
        new BackupService(source.Root, Clock()).Export(bundle, new[] { BackupCategory.Themes });

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.DoesNotContain("passwords", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_TakesPreImportSnapshotBeforeWriting()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":77}""");
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        var service = new BackupService(target.Root, Clock());

        service.Import(bundle, ImportMode.Replace);

        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreImport);
    }

    [Fact]
    public void Import_RejectsCorruptBundleWithoutTouchingDisk()
    {
        using var target = BackupTestTree.CreatePopulated();
        string original = target.ReadFile("settings.json");
        string bogus = Path.Combine(target.Root, "bogus.novabackup");
        File.WriteAllText(bogus, "not a zip");

        var service = new BackupService(target.Root, Clock());
        var outcome = service.Import(bogus, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.CorruptArchive, outcome.Failure);
        Assert.Equal(original, target.ReadFile("settings.json"));
        // Validation happens before anything is touched, so no snapshot was needed.
        Assert.Empty(service.ListSnapshots());
    }

    [Fact]
    public void Import_RejectsNewerSchemaWithoutTouchingDisk()
    {
        using var target = BackupTestTree.CreatePopulated();
        string original = target.ReadFile("settings.json");
        string future = Path.Combine(target.Root, "future.novabackup");
        WriteFutureSchemaBundle(future);

        var outcome = new BackupService(target.Root, Clock()).Import(future, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.UnsupportedSchemaVersion, outcome.Failure);
        Assert.Equal(original, target.ReadFile("settings.json"));
    }

    /// <summary>
    /// A mid-write failure must still leave a pre-import snapshot to roll back to. A directory
    /// parked on a destination file path blocks the write on every OS — unlike a FileShare.None
    /// lock, which does not block rename or delete on POSIX.
    /// </summary>
    [Fact]
    public void Import_FailingMidWrite_LeavesPreImportSnapshot()
    {
        using var source = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        var service = new BackupService(target.Root, Clock());

        // Park a directory where a theme file must be written.
        string blocked = Path.Combine(target.Root, "themes", "solarized.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var outcome = service.Import(bundle, ImportMode.Merge);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreImport);
    }

    /// <summary>
    /// The pre-import snapshot is not just present — it is a genuine rollback: restoring it
    /// after a failed mid-write import must bring back the exact pre-import bytes, including
    /// whatever this same failed Import already managed to overwrite before it hit the blocked
    /// file. This is the difference between "an error was returned" and "the original state is
    /// actually recoverable".
    /// </summary>
    [Fact]
    public void Import_FailingMidWrite_PreImportSnapshotRestoresOriginalSettings()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":999}""");
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        string originalSettings = target.ReadFile("settings.json");
        var clock = Clock();
        var service = new BackupService(target.Root, clock);

        // Park a directory where a theme file must be written, so Themes (which sorts after
        // Settings in BackupCategory order) fails mid-import, after Settings has already been
        // merged onto the live tree.
        string blocked = Path.Combine(target.Root, "themes", "solarized.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var importOutcome = service.Import(bundle, ImportMode.Merge);
        Assert.False(importOutcome.Success);

        // Settings really was overwritten by the partial import — proving the snapshot is doing
        // real work, not just existing alongside an untouched tree.
        Assert.NotEqual(originalSettings, target.ReadFile("settings.json"));

        var preImport = service.ListSnapshots().Single(s => s.Reason == SnapshotReason.PreImport);
        Directory.Delete(blocked);
        clock.Advance(TimeSpan.FromMinutes(1));

        var restoreOutcome = service.Restore(preImport.Id);

        Assert.True(restoreOutcome.Success, restoreOutcome.Message);
        Assert.Equal(originalSettings, target.ReadFile("settings.json"));
    }

    [Fact]
    public void Restore_RollsBackChangedFile()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        tree.WriteFile("settings.json", """{"FontSize":14}""");
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":99}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        var outcome = service.Restore(snapshot!.Id);

        Assert.True(outcome.Success, outcome.Message);
        // Restore is a Replace, which copies the file verbatim — assert on the parsed value
        // rather than formatting, so the test survives a serializer change.
        using var restored = tree.ReadJson("settings.json");
        Assert.Equal(14, restored.RootElement.GetProperty("FontSize").GetInt32());
    }

    [Fact]
    public void Restore_TakesPreRestoreSnapshotFirst()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        var snapshot = service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile("settings.json", """{"FontSize":99}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        service.Restore(snapshot!.Id);

        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreRestore);
    }

    [Fact]
    public void Restore_DropsLocalOnlyItems()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var clock = Clock();
        var service = new BackupService(tree.Root, clock);

        var snapshot = service.Snapshot(SnapshotReason.Auto);
        tree.WriteFile(Path.Combine("themes", "added-later.json"), """{"name":"Later"}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        service.Restore(snapshot!.Id);

        Assert.False(tree.Exists(Path.Combine("themes", "added-later.json")));
    }

    [Fact]
    public void Restore_UnknownIdFails()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, Clock());

        var outcome = service.Restore("auto-19700101T000000Z-deadbeef");

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.NotFound, outcome.Failure);
    }

    private static string ExportFrom(BackupTestTree tree)
    {
        string bundle = Path.Combine(tree.Root, "export.novabackup");
        var outcome = new BackupService(tree.Root, Clock()).Export(bundle);
        Assert.True(outcome.Success, outcome.Message);
        return bundle;
    }

    private static FixedTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));

    private static void WriteFutureSchemaBundle(string path)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(
            $$"""{"schemaVersion":{{BackupManifest.CurrentSchemaVersion + 1}},"appVersion":"9.9.9","createdUtc":"2030-01-01T00:00:00+00:00","machine":"F","categories":["settings"]}""");
    }
}
