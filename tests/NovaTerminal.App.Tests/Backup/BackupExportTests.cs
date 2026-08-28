using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NovaTerminal.Backup;

namespace NovaTerminal.Tests.Backup;

public sealed class BackupExportTests
{
    // Fix round 3 (Codex review): Export_WithRelativeDestination_ResolvesAgainstCurrentDirectory_NotRootDirectory
    // mutates the process-global Environment.CurrentDirectory around a get/set/restore that is
    // not itself atomic. xunit.v3 does not guarantee serial execution of test methods within one
    // class, so another test interleaving its own CWD-relative work between this one's set and
    // its own restore would read the wrong directory - the same hazard BackupToolsTests.EnvVarGate
    // documents for NOVATERM_APPDATA_ROOT. Nothing else in this assembly does CWD-relative file
    // I/O today (BackupTestTree builds only absolute paths by design), so this isn't a live flake
    // - it is a latent trap for the next relative-path test, closed the same way: a private lock
    // around just the one body that touches this global, rather than a whole extra
    // collection-definition type for a single file.
    private static readonly object CurrentDirectoryGate = new();

    [Fact]
    public void Export_WritesBundleWithAllCategoriesByDefault()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "export.novabackup");

        var outcome = service.Export(bundle);

        Assert.True(outcome.Success, outcome.Message);
        Assert.True(File.Exists(bundle));

        var inspection = service.Inspect(bundle);
        Assert.True(inspection.Success, inspection.Message);
        Assert.Equal(
            BackupCatalog.AllCategories.Count,
            inspection.Inspection!.Manifest.Categories.Count);
    }

    [Fact]
    public void Export_StampsManifestFromClock()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "export.novabackup");

        service.Export(bundle);
        var inspection = service.Inspect(bundle);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero),
            inspection.Inspection!.Manifest.CreatedUtc);
        Assert.False(string.IsNullOrWhiteSpace(inspection.Inspection.Manifest.AppVersion));
        Assert.False(string.IsNullOrWhiteSpace(inspection.Inspection.Manifest.Machine));
    }

    [Fact]
    public void Export_SubsetOmitsOtherCategories()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "subset.novabackup");

        service.Export(bundle, new[] { BackupCategory.Themes, BackupCategory.Snippets });
        var inspection = service.Inspect(bundle);

        Assert.Equal(
            new[] { "themes", "snippets" }.Order().ToArray(),
            inspection.Inspection!.Manifest.Categories.Order().ToArray());
        Assert.Equal(0, inspection.Inspection.ItemCounts[BackupCategory.Settings]);
    }

    /// <summary>
    /// Structural guarantee for issue #100: a bundle must never carry secret material.
    /// The sentinel is planted in every place a naive implementation might pick it up.
    /// </summary>
    [Fact]
    public void Export_NeverContainsSecretMaterial()
    {
        const string sentinel = "hunter2-SUPER-SECRET-SENTINEL";
        using var tree = BackupTestTree.CreatePopulated();
        tree.WriteFile("vault.dat", sentinel);
        tree.WriteFile(Path.Combine("command-assist", "history.jsonl"), $$"""{"cmd":"echo {{sentinel}}"}""");
        tree.WriteFile(Path.Combine("logs", "debug.log"), sentinel);

        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "export.novabackup");
        service.Export(bundle);

        byte[] bytes = File.ReadAllBytes(bundle);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(sentinel), bytes);

        // Also assert on decompressed content — compression could hide the raw bytes.
        using var zip = ZipFile.OpenRead(bundle);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain(sentinel, reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Export_SucceedsWhenSomeCategoriesAreAbsent()
    {
        using var tree = BackupTestTree.CreateEmpty();
        tree.WriteFile("settings.json", """{"FontSize":16}""");
        var service = new BackupService(tree.Root, FixedClock());
        string bundle = Path.Combine(tree.Root, "sparse.novabackup");

        var outcome = service.Export(bundle);

        Assert.True(outcome.Success, outcome.Message);
        var inspection = service.Inspect(bundle);
        Assert.True(inspection.Success, inspection.Message);
        Assert.Equal(new[] { "settings" }, inspection.Inspection!.Manifest.Categories);
    }

    [Fact]
    public void Export_FailsGracefullyWhenDestinationUnwritable()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        // A directory parked on the destination path blocks the file write on every OS.
        string blocked = Path.Combine(tree.Root, "blocked.novabackup");
        Directory.CreateDirectory(blocked);

        var outcome = service.Export(blocked);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    [Fact]
    public void Export_FailsGracefullyForEmptyDestination()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export("");

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    [Fact]
    public void Export_FailsGracefullyForWhitespaceDestination()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export("   ");

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// An embedded NUL is rejected by Path.GetFullPath on every platform .NET supports
    /// (unlike other "invalid" characters such as '&lt;' or '|', which are only rejected on
    /// Windows and only much later, by the filesystem) — so this is the one malformed-path
    /// case that behaves identically on Windows and POSIX.
    /// </summary>
    [Fact]
    public void Export_FailsGracefullyForPathWithEmbeddedNul()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string malformed = Path.Combine(tree.Root, "bad\0name.novabackup");

        var outcome = service.Export(malformed);

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// F2 (Codex review, PR #362): a destination that resolves onto a live catalog FILE (not a
    /// directory entry) is the sharpest case - BundleWriter would archive the current
    /// settings.json into its temp bundle, then <c>File.Move(overwrite: true)</c> replaces the
    /// live file with ZIP bytes, and Export used to still report success. Asserts both that Export
    /// now fails AND that the live file's content survives untouched - the second assertion is
    /// what actually catches a regression back to the old "reject the intent, still clobber the
    /// file first" bug were the guard placed after BundleWriter ran instead of before it.
    /// </summary>
    [Fact]
    public void Export_ToLiveSettingsFile_IsRejected_AndLeavesTheLiveFileIntact()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string original = tree.ReadFile("settings.json");
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(Path.Combine(tree.Root, "settings.json"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.Equal(original, tree.ReadFile("settings.json"));
    }

    /// <summary>
    /// Same aliasing hazard, for a directory-shaped catalog entry (Themes): a destination nested
    /// under "themes/" is still "at-or-under" the catalog source per the finding's wording, even
    /// though it does not exactly equal the directory itself.
    ///
    /// Requests only the Settings category (excluding Themes) so <c>BundleWriter</c> never
    /// enumerates the themes directory itself - otherwise its own temp-sibling-then-move write
    /// (the temp file briefly lives right next to the destination) would pick up its own
    /// in-progress temp file mid-enumeration and fail with an unrelated <c>IOException</c>
    /// regardless of this guard, defeating the point of a targeted regression test. The guard
    /// under test here must fire from a full walk of <c>BackupCatalog.Entries</c>, independent of
    /// which categories were actually requested.
    /// </summary>
    [Fact]
    public void Export_ToPathUnderLiveThemesDirectory_IsRejected()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(
            Path.Combine(tree.Root, "themes", "sneaky.novabackup"),
            new[] { BackupCategory.Settings });

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        Assert.False(File.Exists(Path.Combine(tree.Root, "themes", "sneaky.novabackup")));
    }

    /// <summary>
    /// The connections/native_known_hosts.json catalog entry, exercised separately from
    /// settings.json so the guard is proven to walk every <c>BackupCatalog.Entries</c> row, not
    /// just the first one checked.
    /// </summary>
    [Fact]
    public void Export_ToLiveConnectionsFile_IsRejected()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(Path.Combine(tree.Root, "ssh", "native_known_hosts.json"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// Design decision (documented in the fix's own remarks on
    /// <c>BackupService.TryDescribeProtectedDestination</c>): <see cref="BackupService.BackupsDirectory"/>
    /// is rejected too, even though it is not itself a <c>BackupCatalog.Entries</c> source. It is
    /// where <c>Snapshot</c> writes the pre-import/pre-restore rollback points <c>Restore</c>
    /// depends on; an Export landing on an existing snapshot's file name would silently destroy
    /// that rollback point the same way an aliased catalog entry destroys live configuration.
    /// </summary>
    [Fact]
    public void Export_IntoBackupsDirectory_IsRejected()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        Directory.CreateDirectory(service.BackupsDirectory);

        var outcome = service.Export(Path.Combine(service.BackupsDirectory, "sneaky.novabackup"));

        Assert.False(outcome.Success);
        Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
    }

    /// <summary>
    /// A destination that merely shares a name PREFIX with a catalog entry (rather than being the
    /// entry itself or nested under it) must still be allowed - "settings.json.export" is not
    /// "settings.json", and a naive <c>StartsWith</c> without a separator boundary would wrongly
    /// reject it.
    /// </summary>
    [Fact]
    public void Export_ToPathWithNamePrefixOfCatalogEntry_IsStillAllowed()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());

        var outcome = service.Export(Path.Combine(tree.Root, "settings.json.export"));

        Assert.True(outcome.Success, outcome.Message);
    }

    /// <summary>
    /// Fix round 2 (Codex review, PR #362): TryDescribeProtectedDestination used to resolve a
    /// relative destinationPath against RootDirectory
    /// (Path.GetFullPath(destinationPath, RootDirectory)), but BundleWriter.Write hands the raw
    /// string straight to FileStream/File.Move, which resolve a relative path against the
    /// process's CURRENT WORKING DIRECTORY instead - a different base entirely. That mismatch
    /// partially defeated the F2 fix: reachable from the CLI (BackupCommand.Execute passes the
    /// bundle path through to Export verbatim), a caller running with CWD set to "&lt;root&gt;/ssh"
    /// and exporting the bare relative filename "profiles.json" had it checked against
    /// "&lt;root&gt;/profiles.json" (not a catalog entry - allowed) while BundleWriter actually wrote
    /// to "&lt;root&gt;/ssh/profiles.json" - the live Connections file - destroying it exactly like
    /// the original F2 defect. Sets the real process CWD, since that is the only way to exercise
    /// BundleWriter's actual resolution path; restores it in a finally, since other tests in this
    /// process share it.
    /// </summary>
    [Fact]
    public void Export_WithRelativeDestination_ResolvesAgainstCurrentDirectory_NotRootDirectory()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock());
        string sshDir = Path.Combine(tree.Root, "ssh");

        // Fix round 3: serialize the whole get/set-CWD/act/restore-CWD sequence under
        // CurrentDirectoryGate - see the field's own remarks for why an unguarded mutation of
        // this process-global is a latent trap even though nothing collides with it today.
        lock (CurrentDirectoryGate)
        {
            string originalCwd = Environment.CurrentDirectory;

            try
            {
                Environment.CurrentDirectory = sshDir;

                // Relative to CWD ("<root>/ssh"), this resolves to "<root>/ssh/profiles.json" -
                // the live Connections catalog file - not "<root>/profiles.json" (not a catalog
                // path at all), which is what the old RootDirectory-based guard incorrectly
                // checked instead.
                var outcome = service.Export("profiles.json");

                Assert.False(outcome.Success);
                Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
            }
            finally
            {
                Environment.CurrentDirectory = originalCwd;
            }
        }
    }

    /// <summary>
    /// F3 (Codex review round 2, PR #362): <c>var present = requested.Where(HasContent).ToArray();</c>
    /// used to run BEFORE ExportCore's try block. <c>HasContent</c> enumerates every
    /// directory-category's files (<c>Directory.EnumerateFiles(...).Any()</c>), so an inaccessible
    /// category directory threw <see cref="UnauthorizedAccessException"/> straight out of
    /// <c>Export</c> instead of the documented <see cref="BackupFailureKind.WriteFailed"/> outcome.
    /// Denies read/list access to the "themes" catalog directory (a real directory-shaped category)
    /// and verifies — rather than assumes — that this actually blocks enumeration before asserting
    /// on it, the same "decide the skip by trying it" pattern
    /// <c>SettingsWindowBackupSectionTests.TryBlockDirectoryListing</c> uses for the analogous
    /// ListSnapshots gap.
    /// </summary>
    [Fact]
    public void Export_WhenACategoryDirectoryCannotBeEnumerated_ReturnsWriteFailed_InsteadOfThrowing()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string themesDirectory = Path.Combine(tree.Root, "themes");

        bool blocked = TryBlockDirectoryListing(themesDirectory, out Action restore);
        try
        {
            if (!blocked)
            {
                Assert.Skip("this process can enumerate a directory it just denied itself access to (root, or an unrestricted account)");
            }

            var service = new BackupService(tree.Root, FixedClock());
            string bundle = Path.Combine(tree.Root, "export.novabackup");

            BackupOutcome? outcome = null;
            var thrown = Record.Exception(() => outcome = service.Export(bundle));

            Assert.Null(thrown);
            Assert.NotNull(outcome);
            Assert.False(outcome!.Success);
            Assert.Equal(BackupFailureKind.WriteFailed, outcome.Failure);
        }
        finally
        {
            restore();
        }
    }

    /// <summary>
    /// Section A backstop (Codex review round 2, PR #362): real filesystem/zip APIs essentially
    /// only ever throw from the types <c>ExportCore</c>'s own specific catch already filters on
    /// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>,
    /// <see cref="ArgumentException"/>), so an "unrecognized exception type" cannot be provoked
    /// portably and deterministically through the real filesystem — the same reasoning
    /// <c>BackupCommitRollbackTests</c> documents for the analogous commit-phase seam. This test
    /// seam (<see cref="BackupService.SimulateExportFailureForTest"/>) simulates the escape
    /// directly: whatever throws, the backstop must convert it into a typed
    /// <see cref="BackupFailureKind.Unexpected"/> failure rather than letting it fault the caller.
    /// </summary>
    [Fact]
    public void Export_WhenAnUnrecognizedExceptionEscapes_ReturnsATypedUnexpectedFailure_InsteadOfThrowing()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root, FixedClock())
        {
            SimulateExportFailureForTest = () => new InvalidOperationException("simulated exotic export failure"),
        };
        string bundle = Path.Combine(tree.Root, "export.novabackup");

        BackupOutcome? outcome = null;
        var thrown = Record.Exception(() => outcome = service.Export(bundle));

        Assert.Null(thrown);
        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.Equal(BackupFailureKind.Unexpected, outcome.Failure);
        Assert.Contains("simulated exotic export failure", outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(bundle));
    }

    /// <summary>
    /// Denies directory-listing access to <paramref name="directory"/> for the current process,
    /// verifying - rather than assuming - that this actually blocks <c>Directory.EnumerateFiles</c>
    /// before reporting success. The returned <paramref name="restore"/> action always undoes the
    /// change, whether or not the block took, so temp-directory cleanup can proceed either way.
    /// Mirrors <c>SettingsWindowBackupSectionTests.TryBlockDirectoryListing</c> exactly - duplicated
    /// locally rather than shared, matching this codebase's existing convention of small
    /// per-file test helpers (e.g. <c>FixedTimeProvider</c> below).
    /// </summary>
    private static bool TryBlockDirectoryListing(string directory, out Action restore)
    {
        if (OperatingSystem.IsWindows())
        {
            var dirInfo = new DirectoryInfo(directory);
            var security = dirInfo.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var rule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.ListDirectory | FileSystemRights.Read,
                AccessControlType.Deny);

            security.AddAccessRule(rule);
            dirInfo.SetAccessControl(security);

            restore = () =>
            {
                try
                {
                    var current = dirInfo.GetAccessControl();
                    current.RemoveAccessRule(rule);
                    dirInfo.SetAccessControl(current);
                }
                catch
                {
                    // Best-effort restore; the temp tree's Dispose is best-effort too.
                }
            };
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);

            restore = () =>
            {
                try
                {
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    // Best-effort restore; the temp tree's Dispose is best-effort too.
                }
            };
        }

        try
        {
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();
            return false; // enumeration still succeeded - the restriction did not take (root, etc.)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static TimeProvider FixedClock() =>
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 9, 14, 0, TimeSpan.Zero));
}

/// <summary>Deterministic clock so manifest timestamps and snapshot ids are assertable.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
