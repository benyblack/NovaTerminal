using NovaTerminal.Backup;

namespace NovaTerminal.Tests.Backup;

/// <summary>
/// Final whole-branch review, I3 and Deferred minor #5: the commit-phase undo journal that makes
/// a mid-import failure "self-healing" (Phase 2 of <c>BackupService.Import</c>) has two gaps
/// neither per-task review could see in isolation:
///  - RollBack's own boolean return - which decides whether staging is preserved and what the
///    user is told - had no direct test.
///  - An exception type outside the set CommitWithUndo's per-step catch filters on
///    (IOException/UnauthorizedAccessException/ArgumentException/NotSupportedException) used to
///    escape uncaught, skip RollBack entirely, and then be swept away by ImportCore's own
///    unconditional staging cleanup - destroying the last recovery path right when it mattered
///    most.
/// </summary>
public sealed class BackupCommitRollbackTests
{
    /// <summary>
    /// Deferred minor #5. A hand-built journal entry whose undo path does not exist makes the
    /// per-entry <see cref="File.Move(string, string)"/> throw <see cref="FileNotFoundException"/>,
    /// swallowed by RollBack's own catch-all into a false return - deterministic on every OS,
    /// unlike trying to provoke the same failure through a real multi-step commit.
    /// </summary>
    [Fact]
    public void RollBack_WhenAnEntryCannotBeRestored_ReturnsFalse()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string livePath = Path.Combine(tree.Root, "restored-target.json");
        string missingUndoPath = Path.Combine(tree.Root, "undo-does-not-exist.json");

        var journal = new List<(string LivePath, string UndoPath, bool IsDirectory, bool HadOriginal)>
        {
            (livePath, missingUndoPath, false, true),
        };

        var messages = new List<string>();
        bool result = BackupService.RollBack(journal, messages.Add);

        Assert.False(result);
        Assert.Contains(messages, m => m.Contains(livePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the same guarantee, for contrast: when every journaled entry really can
    /// be restored, RollBack must report success.
    /// </summary>
    [Fact]
    public void RollBack_WhenEveryEntryCanBeRestored_ReturnsTrue()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string livePath = Path.Combine(tree.Root, "restored-target.json");
        string undoPath = Path.Combine(tree.Root, "undo-original.json");
        File.WriteAllText(undoPath, "original content");

        var journal = new List<(string LivePath, string UndoPath, bool IsDirectory, bool HadOriginal)>
        {
            (livePath, undoPath, false, true),
        };

        bool result = BackupService.RollBack(journal, _ => { });

        Assert.True(result);
        Assert.Equal("original content", File.ReadAllText(livePath));
    }

    /// <summary>
    /// I3: real File/Directory APIs essentially only ever throw from the four types
    /// CommitWithUndo's per-step catch already filters on, so an "unrecognized exception type"
    /// cannot be provoked portably and deterministically through the real filesystem. This test
    /// seam (<see cref="BackupService.SimulateCommitPhaseFailureForTest"/>, the same established
    /// pattern as <c>SnapshotScheduler.BeforeSnapshotForTest</c>) simulates the escape directly:
    /// whatever throws, and however it throws, ImportCore's outer catch-all must preserve staging
    /// rather than let its own unconditional cleanup destroy the only surviving copy of any
    /// already-committed step's original content.
    /// </summary>
    [Fact]
    public void Import_WhenCommitPhaseThrowsAnUnrecognizedException_PreservesStagingForManualRecovery()
    {
        using var tree = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(Path.GetTempPath(), $"nova_backup_test_{Guid.NewGuid():N}.novabackup");
        try
        {
            Assert.True(new BackupService(tree.Root).Export(bundle).Success);

            var service = new BackupService(tree.Root)
            {
                SimulateCommitPhaseFailureForTest = () => new InvalidOperationException("simulated exotic commit failure"),
            };

            var thrown = Record.Exception(() => service.Import(bundle, ImportMode.Replace));

            Assert.NotNull(thrown);
            Assert.IsType<InvalidOperationException>(thrown);

            // The staging directory (.import-<guid> under the root) must still be on disk -
            // proving the catch-all preserved it instead of ImportCore's finally deleting it out
            // from under the failure.
            string[] stagingDirs = Directory.GetDirectories(tree.Root, ".import-*");
            Assert.Single(stagingDirs);
        }
        finally
        {
            try { if (File.Exists(bundle)) File.Delete(bundle); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Contrast case, using the same real (not simulated) mid-commit failure the existing
    /// <c>Import_FailingMidWrite_SelfHealsWithoutExplicitRestore</c> coverage already drives: a
    /// RECOGNIZED failure type still goes through the normal <c>ImportCommitException</c> path
    /// and is reported as a typed failure outcome rather than an escaped exception. The I3
    /// catch-all must not change behavior for the case that was already handled correctly -
    /// asserted here directly against staging rather than only against the live tree, since that
    /// is the exact thing the catch-all's <c>preserveStagingForManualRecovery</c> flag controls:
    /// a successful rollback must still let staging be cleaned up normally.
    /// </summary>
    [Fact]
    public void Import_WhenRollbackSucceeds_StillReturnsATypedFailure_AndCleansUpStaging()
    {
        using var source = BackupTestTree.CreatePopulated();
        string bundle = Path.Combine(Path.GetTempPath(), $"nova_backup_test_{Guid.NewGuid():N}.novabackup");
        try
        {
            Assert.True(new BackupService(source.Root).Export(bundle).Success);

            using var target = BackupTestTree.CreatePopulated();
            var service = new BackupService(target.Root);

            // Settings and Themes both sort before Connections, so both commit to the live tree
            // before this blocked category fails - a real IOException, recognized by
            // CommitWithUndo's own per-step catch, which then runs RollBack successfully.
            string blocked = Path.Combine(target.Root, "ssh", "profiles.json");
            File.Delete(blocked);
            Directory.CreateDirectory(blocked);

            BackupOutcome? outcome = null;
            var thrown = Record.Exception(() => outcome = service.Import(bundle, ImportMode.Replace));

            Assert.Null(thrown);
            Assert.NotNull(outcome);
            Assert.False(outcome!.Success);

            // Rollback succeeded, so staging must be cleaned up as usual - not preserved as if
            // manual recovery were needed.
            Assert.Empty(Directory.GetDirectories(target.Root, ".import-*"));
        }
        finally
        {
            try { if (File.Exists(bundle)) File.Delete(bundle); } catch { /* best-effort cleanup */ }
        }
    }
}
