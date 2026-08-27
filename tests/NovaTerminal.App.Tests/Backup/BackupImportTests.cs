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

    /// <summary>
    /// I4: malformed LOCAL content must not abort the import — it is treated as absent so the
    /// bundle wins wholesale, exactly as if the file did not exist. (Malformed BUNDLE content is
    /// the opposite case — see Import_RejectsBundleWithZipSlipEntry_AsTypedFailureNotThrow for a
    /// bundle-side corruption — and correctly aborts instead.)
    /// </summary>
    [Fact]
    public void Merge_Settings_CorruptLocalJson_BundleWinsWholesale()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":42}""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", "{not json");
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        Assert.True(outcome.Success, outcome.Message);
        using var settings = target.ReadJson("settings.json");
        Assert.Equal(42, settings.RootElement.GetProperty("FontSize").GetInt32());
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

    /// <summary>
    /// N4: a Merge must keep the user's own local-only ".bak" sibling like any other local-only
    /// file. Skipping ".bak" is only meant to stop the bundle from propagating one forward — it
    /// must not be applied to the live side too, or the whole point of "local-only survives" is
    /// defeated for exactly the files AtomicFile itself creates.
    /// </summary>
    [Fact]
    public void Merge_Themes_KeepsLocalOnlyBakFile()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "mytheme.json.bak"), """{"name":"Backup"}""");
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        Assert.True(outcome.Success, outcome.Message);
        Assert.True(target.Exists(Path.Combine("themes", "mytheme.json.bak")));
        Assert.True(target.Exists(Path.Combine("themes", "solarized.json")));
    }

    [Fact]
    public void Merge_Workspaces_KeepsLocalOnlyWorkspace()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("workspaces", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        Assert.True(target.Exists(Path.Combine("workspaces", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("workspaces", "default.json")));
    }

    [Fact]
    public void Replace_Workspaces_DropsLocalOnlyWorkspace()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("workspaces", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.False(target.Exists(Path.Combine("workspaces", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("workspaces", "default.json")));
    }

    [Fact]
    public void Merge_Policy_KeepsLocalOnlyPolicyFile()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("policy", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        Assert.True(target.Exists(Path.Combine("policy", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("policy", "workspace_policy.json")));
    }

    [Fact]
    public void Replace_Policy_DropsLocalOnlyPolicyFile()
    {
        using var source = BackupTestTree.CreatePopulated();
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("policy", "local-only.json"), """{"name":"LocalOnly"}""");
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.False(target.Exists(Path.Combine("policy", "local-only.json")));
        Assert.True(target.Exists(Path.Combine("policy", "workspace_policy.json")));
    }

    /// <summary>
    /// I6: Replace must clear a live directory even when the bundle donates nothing for it — an
    /// absent/empty staged directory still means "the bundle is the truth: nothing", not "leave
    /// local content alone". Workspaces still exports/imports as a category here because its
    /// sibling directory (workspaces/) has content, even though workspace_templates/ is empty.
    /// </summary>
    [Fact]
    public void Replace_EmptySourceDirectory_ClearsLiveDirectory()
    {
        using var source = BackupTestTree.CreatePopulated();
        Directory.Delete(Path.Combine(source.Root, "workspace_templates"), recursive: true);
        using var target = BackupTestTree.CreatePopulated();
        Assert.True(target.Exists(Path.Combine("workspace_templates", "dev.json")));
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        Assert.True(outcome.Success, outcome.Message);
        Assert.False(target.Exists(Path.Combine("workspace_templates", "dev.json")));
        // The sibling directory in the same category, which the bundle DID carry, still landed.
        Assert.True(target.Exists(Path.Combine("workspaces", "default.json")));
    }

    [Fact]
    public void Merge_Connections_MatchesProfilesById()
    {
        string sharedId = "11111111-1111-1111-1111-111111111111";
        string bundleOnlyId = "22222222-2222-2222-2222-222222222222";
        string localOnlyId = "33333333-3333-3333-3333-333333333333";

        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"SchemaVersion":1,"Profiles":[
              {"Id":"{{sharedId}}","Name":"From Bundle","Host":"bundle.example"},
              {"Id":"{{bundleOnlyId}}","Name":"Bundle Only","Host":"only.example"}]}
            """);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"SchemaVersion":1,"Profiles":[
              {"Id":"{{sharedId}}","Name":"Local Version","Host":"local.example"},
              {"Id":"{{localOnlyId}}","Name":"Local Only","Host":"keep.example"}]}
            """);

        string bundle = ExportFrom(source);
        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);

        using var doc = target.ReadJson(Path.Combine("ssh", "profiles.json"));
        var profiles = doc.RootElement.GetProperty("Profiles").EnumerateArray().ToArray();

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
            {"SchemaVersion":1,"Profiles":[{"Id":"22222222-2222-2222-2222-222222222222","Name":"Bundle Only"}]}
            """);
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "profiles.json"), """
            {"SchemaVersion":1,"Profiles":[{"Id":"33333333-3333-3333-3333-333333333333","Name":"Local Only"}]}
            """);
        string bundle = ExportFrom(source);

        new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Replace);

        using var doc = target.ReadJson(Path.Combine("ssh", "profiles.json"));
        var profiles = doc.RootElement.GetProperty("Profiles").EnumerateArray().ToArray();
        Assert.Single(profiles);
        Assert.Equal("Bundle Only", profiles[0].GetProperty("Name").GetString());
    }

    /// <summary>
    /// C1: the real profiles.json on disk is PascalCase ("SchemaVersion"/"Profiles" —
    /// JsonSshProfileStore's SshJsonContext sets no naming policy) and JsonNode's indexer is
    /// case-sensitive. A hardcoded lowercase "profiles" read/write would silently miss the real
    /// property and add a second, empty one next to it, discarding every bundle profile while
    /// still reporting success. This proves a legacy/foreign-tool lowercase shape is normalized
    /// in place instead — exactly one profiles key survives, not two.
    /// </summary>
    [Fact]
    public void Merge_Connections_LegacyLowercaseProfilesKey_IsNormalizedNotDuplicated()
    {
        string localOnlyId = "33333333-3333-3333-3333-333333333333";
        string bundleOnlyId = "22222222-2222-2222-2222-222222222222";

        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"SchemaVersion":1,"Profiles":[{"Id":"{{bundleOnlyId}}","Name":"Bundle Only"}]}
            """);

        using var target = BackupTestTree.CreatePopulated();
        // Legacy/foreign shape: lowercase "profiles" — real files are never actually shaped
        // this way (JsonSshProfileStore only ever writes PascalCase), but nothing should
        // silently corrupt the file if one is ever encountered.
        target.WriteFile(Path.Combine("ssh", "profiles.json"), $$"""
            {"schemaVersion":1,"profiles":[{"Id":"{{localOnlyId}}","Name":"Local Only"}]}
            """);

        string bundle = ExportFrom(source);
        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);
        Assert.True(outcome.Success, outcome.Message);

        using var doc = JsonDocument.Parse(target.ReadFile(Path.Combine("ssh", "profiles.json")));
        var profilesProperties = doc.RootElement.EnumerateObject()
            .Where(p => string.Equals(p.Name, "profiles", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Single(profilesProperties);

        var profiles = profilesProperties[0].Value.EnumerateArray().ToArray();
        Assert.Equal(2, profiles.Length);
        Assert.Contains(profiles, p => p.GetProperty("Id").GetString() == localOnlyId);
        Assert.Contains(profiles, p => p.GetProperty("Id").GetString() == bundleOnlyId);
    }

    [Fact]
    public void Merge_NativeKnownHosts_UnionsAndDedupes()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("ssh", "native_known_hosts.json"), """[{"Host":"b"},{"Host":"c"}]""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("ssh", "native_known_hosts.json"), """[{"Host":"a"},{"Host":"b"}]""");
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, ImportMode.Merge);
        Assert.True(outcome.Success, outcome.Message);

        using var doc = target.ReadJson(Path.Combine("ssh", "native_known_hosts.json"));
        var hosts = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("Host").GetString()).ToArray();

        Assert.Equal(3, hosts.Length);
        Assert.Equal(1, hosts.Count(h => h == "b"));
        Assert.Contains("a", hosts);
        Assert.Contains("c", hosts);
    }

    /// <summary>
    /// Snippets is a flat array with no stable id, so merge and replace are deliberately the
    /// same operation: wholesale bundle-replaces-local in both modes. This is what would catch
    /// someone later "improving" it into an array merge — a merge would keep "local-only" too.
    /// </summary>
    [Theory]
    [InlineData(ImportMode.Merge)]
    [InlineData(ImportMode.Replace)]
    public void Snippets_AlwaysReplacedWholesale(ImportMode mode)
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile(Path.Combine("command-assist", "snippets.json"), """[{"name":"from-bundle"}]""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("command-assist", "snippets.json"), """[{"name":"local-only"}]""");
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock()).Import(bundle, mode);
        Assert.True(outcome.Success, outcome.Message);

        Assert.Equal(
            source.ReadFile(Path.Combine("command-assist", "snippets.json")),
            target.ReadFile(Path.Combine("command-assist", "snippets.json")));
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

    /// <summary>
    /// The <c>categories</c> parameter narrows what gets applied even when the bundle carries
    /// more — a caller asking to import only Settings must not also get Themes clobbered.
    /// </summary>
    [Fact]
    public void Import_WithExplicitCategories_OnlyAppliesRequestedOnes()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":55}""");
        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "local-only.json"), """{"name":"LocalOnly"}""");
        string originalThemeFile = target.ReadFile(Path.Combine("themes", "solarized.json"));
        string bundle = ExportFrom(source);

        var outcome = new BackupService(target.Root, Clock())
            .Import(bundle, ImportMode.Replace, new[] { BackupCategory.Settings });

        Assert.True(outcome.Success, outcome.Message);
        using var settings = target.ReadJson("settings.json");
        Assert.Equal(55, settings.RootElement.GetProperty("FontSize").GetInt32());

        // Themes was in the bundle but not requested — must be untouched, including the
        // local-only file that a Themes Replace would have deleted.
        Assert.True(target.Exists(Path.Combine("themes", "local-only.json")));
        Assert.Equal(originalThemeFile, target.ReadFile(Path.Combine("themes", "solarized.json")));
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

    /// <summary>
    /// N1: pins the invariant that makes a cross-volume import failure structurally impossible.
    /// The scratch staging tree must be a descendant of RootDirectory, never of
    /// Path.GetTempPath() — Phase 2's commit moves directories with Directory.Move, which is a
    /// bare rename and throws IOException across a volume boundary on both Windows and Unix
    /// (unlike File.Move, which falls back to copy+delete). TEMP commonly sits on a different
    /// drive from an app-data root on Windows, so staging there made every directory-category
    /// import fail outright on an ordinary machine — and no test whose whole tree lives under
    /// one temp root already (as BackupTestTree's does) can see that regression by exercising
    /// Import's outward behavior alone, hence pinning the structural property directly.
    /// </summary>
    [Fact]
    public void ImportStagingRoot_ResolvesUnderRootDirectory_NotUnderTempDirectory()
    {
        using var target = BackupTestTree.CreatePopulated();
        var service = new BackupService(target.Root, Clock());

        string staging = service.ResolveImportStagingRoot();

        string relative = Path.GetRelativePath(target.Root, staging);
        Assert.False(relative.StartsWith("..", StringComparison.Ordinal), $"staging '{staging}' escaped RootDirectory '{target.Root}'");
        Assert.False(Path.IsPathRooted(relative), $"staging '{staging}' escaped RootDirectory '{target.Root}'");
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

        var service = new BackupService(target.Root, Clock());
        var outcome = service.Import(future, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.UnsupportedSchemaVersion, outcome.Failure);
        Assert.Equal(original, target.ReadFile("settings.json"));
        // Validation happens before anything is touched, so no snapshot was needed.
        Assert.Empty(service.ListSnapshots());
    }

    /// <summary>
    /// N2: BundleReader.ExtractTo throws InvalidDataException from its own zip-slip guard (and
    /// would from a corrupt per-entry deflate stream too), but Inspect only reads the manifest
    /// and central directory, so it cannot catch this. Extraction happens inside ImportCore, so
    /// this must come back as a typed CorruptArchive failure, not an exception escaping Import —
    /// these methods have a return-typed-outcome contract, not a throwing one. Reuses the same
    /// zip-slip entry shape BundleReader's own tests use, wrapped in a bundle whose manifest is
    /// otherwise entirely valid, so Inspect's manifest/count checks pass and extraction is what
    /// actually fails.
    /// </summary>
    [Fact]
    public void Import_RejectsBundleWithZipSlipEntry_AsTypedFailureNotThrow()
    {
        using var target = BackupTestTree.CreatePopulated();
        string original = target.ReadFile("settings.json");
        string bundle = Path.Combine(target.Root, "zip-slip.novabackup");
        WriteZipSlipBundle(bundle);

        var service = new BackupService(target.Root, Clock());
        var outcome = service.Import(bundle, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.CorruptArchive, outcome.Failure);
        Assert.Equal(original, target.ReadFile("settings.json"));
    }

    /// <summary>
    /// C2: if the forced pre-import snapshot cannot be written (e.g. the backups directory
    /// cannot even be created), Import must refuse rather than proceed without a rollback point
    /// — otherwise the failure-path message "a pre-import snapshot was taken" would be a lie,
    /// and a mid-write failure would have nothing to roll back to. Snapshot() is documented to
    /// never throw and to return null on failure instead, so this is reachable, not theoretical.
    /// </summary>
    [Fact]
    public void Import_RefusesWhenPreImportSnapshotCannotBeWritten()
    {
        using var source = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        string originalSettings = target.ReadFile("settings.json");

        // Park a FILE at the backups path, so Snapshot()'s Directory.CreateDirectory throws
        // internally and Snapshot() swallows it, returning null.
        File.WriteAllText(Path.Combine(target.Root, "backups"), "blocking file");

        var service = new BackupService(target.Root, Clock());
        var outcome = service.Import(bundle, ImportMode.Replace);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Equal(originalSettings, target.ReadFile("settings.json"));
    }

    /// <summary>
    /// A mid-write failure must still leave a pre-import snapshot to roll back to. A directory
    /// parked on a destination FILE path blocks the write on every OS — unlike a FileShare.None
    /// lock, which does not block rename or delete on POSIX. This targets ssh/profiles.json (a
    /// per-file swap) rather than a file inside themes/ (a directory category): under the
    /// rename-based commit, a whole catalog directory is swapped in one Directory.Move, so an
    /// obstruction nested inside it no longer blocks anything — it just gets renamed away along
    /// with everything else.
    /// </summary>
    [Fact]
    public void Import_FailingMidWrite_LeavesPreImportSnapshot()
    {
        using var source = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        var service = new BackupService(target.Root, Clock());

        string blocked = Path.Combine(target.Root, "ssh", "profiles.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var outcome = service.Import(bundle, ImportMode.Merge);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Contains(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreImport);
    }

    /// <summary>
    /// C3: a mid-write failure is now a self-healing, in-call rollback — not merely "recoverable
    /// via a separate Restore of the pre-import snapshot". Settings and Themes both sort before
    /// Connections (the blocked category) and so both get committed to the live tree before the
    /// failure; the automatic rollback must revert both, proving it walks the whole journal
    /// rather than stopping at the first entry.
    /// </summary>
    [Fact]
    public void Import_FailingMidWrite_SelfHealsWithoutExplicitRestore()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":999}""");
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        string originalSettings = target.ReadFile("settings.json");
        string originalTheme = target.ReadFile(Path.Combine("themes", "solarized.json"));
        var service = new BackupService(target.Root, Clock());

        string blocked = Path.Combine(target.Root, "ssh", "profiles.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var outcome = service.Import(bundle, ImportMode.Merge);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Equal(originalSettings, target.ReadFile("settings.json"));
        Assert.Equal(originalTheme, target.ReadFile(Path.Combine("themes", "solarized.json")));
    }

    /// <summary>
    /// I5: Replace must not delete live content before the replacement is ready to take its
    /// place. The rename-based commit renames the old directory aside rather than deleting it
    /// outright, so even in Replace mode — which is SUPPOSED to drop local-only themes on
    /// success — a rolled-back failure must restore the original directory in full (local-only
    /// file included), not leave the user with an empty or half-replaced themes directory.
    /// </summary>
    [Fact]
    public void Import_ReplaceFailingMidWrite_RollsBackThemesRatherThanLeavingItEmpty()
    {
        using var source = BackupTestTree.CreatePopulated();
        string bundle = ExportFrom(source);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile(Path.Combine("themes", "local-only.json"), """{"name":"LocalOnly"}""");
        string originalTheme = target.ReadFile(Path.Combine("themes", "solarized.json"));
        var service = new BackupService(target.Root, Clock());

        string blocked = Path.Combine(target.Root, "ssh", "profiles.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var outcome = service.Import(bundle, ImportMode.Replace);

        Assert.False(outcome.Success);
        // Rolled back to the exact original directory, including the local-only file a
        // successful Replace would have dropped — proving nothing was deleted-then-abandoned.
        Assert.True(target.Exists(Path.Combine("themes", "local-only.json")));
        Assert.Equal(originalTheme, target.ReadFile(Path.Combine("themes", "solarized.json")));
    }

    /// <summary>
    /// Even though a mid-write failure now self-heals within the same Import call (see
    /// <see cref="Import_FailingMidWrite_SelfHealsWithoutExplicitRestore"/>), the pre-import
    /// snapshot it also takes must still be a genuine, independent rollback in its own right —
    /// restoring it must reproduce the exact pre-import bytes, not merely "not error". This is
    /// the difference between "an error was returned" and "the original state is actually
    /// recoverable", including through a completely separate code path (Restore) than the one
    /// that already healed it.
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

        string blocked = Path.Combine(target.Root, "ssh", "profiles.json");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);

        var importOutcome = service.Import(bundle, ImportMode.Merge);
        Assert.False(importOutcome.Success);

        // The in-call rollback already healed this by the time Import returned.
        Assert.Equal(originalSettings, target.ReadFile("settings.json"));

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
        // An unknown id must fail before touching anything, including before taking the
        // pre-restore snapshot that a real restore would take.
        Assert.DoesNotContain(service.ListSnapshots(), s => s.Reason == SnapshotReason.PreRestore);
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

    /// <summary>
    /// A manifest that is otherwise entirely valid (current schema, "themes" declared and
    /// backed by a real entry count) paired with a raw zip entry name that climbs out of the
    /// destination on extraction — the same shape BundleReader's own zip-slip tests use. The raw
    /// entry name literally starts with "themes/", so it also satisfies Inspect's per-category
    /// item-count check; the escape is only caught later, during ExtractTo.
    /// </summary>
    private static void WriteZipSlipBundle(string path)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            writer.Write(
                $$"""{"schemaVersion":{{BackupManifest.CurrentSchemaVersion}},"appVersion":"1.0.0","createdUtc":"2026-08-27T00:00:00+00:00","machine":"X","categories":["themes"]}""");
        }

        var escapingEntry = zip.CreateEntry("themes/../../evil.txt");
        using (var stream = escapingEntry.Open())
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes("payload"));
        }
    }
}
