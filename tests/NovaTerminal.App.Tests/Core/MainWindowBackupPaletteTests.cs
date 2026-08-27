using System.Reflection;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell;
using NovaTerminal.Platform.Backup;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Task 9: the three "Backup" command-palette entries, and the <see cref="SnapshotScheduler"/>
/// MainWindow starts alongside them.
///
/// The scheduler used to not exist at all in the running app - it was built in earlier tasks but
/// nothing called <c>Start()</c>, so automatic snapshots never happened. The critical regression
/// these tests guard is the placement: the scheduler must start from the constructor, not from
/// <c>SetupCommandPalette()</c>, which is lazy (runs on palette-open / settings-save) - starting it
/// there would mean automatic snapshots only begin after the user's first palette open.
/// </summary>
public sealed class MainWindowBackupPaletteTests
{
    [AvaloniaFact]
    public void CommandPalette_IncludesTheThreeBackupEntries_AllRoutedToOpenSettingsToBackupPage()
    {
        CommandRegistry.Clear();
        var window = TestMainWindowFactory.Create();
        try
        {
            var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic)!;
            toggleMethod.Invoke(window, null);

            var backupCommands = CommandRegistry.GetCommands().Where(c => c.Category == "Backup").ToList();

            Assert.Equal(3, backupCommands.Count);

            (string Id, string Title)[] expected =
            [
                ("backup.export", "Export configuration…"),
                ("backup.import", "Import configuration…"),
                ("backup.restore", "Restore from snapshot…"),
            ];

            foreach (var (id, title) in expected)
            {
                var command = Assert.Single(backupCommands, c => c.Id == id);
                Assert.Equal(title, command.Title);

                // All three route to the same handler rather than three separate ones - checked via
                // the delegate's Method identity, not by invoking it: invoking reaches
                // OpenSettingsToBackupPage -> OpenSettings -> a real Window.ShowDialog, which this
                // repo's headless test host cannot return from (see MainWindowShellExitTests'
                // remarks on the same ShowDialog hazard for a different dialog).
                Assert.Equal("OpenSettingsToBackupPage", command.Action.Method.Name);
                Assert.Same(window, command.Action.Target);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_Constructor_CreatesAndStartsExactlyOneSnapshotScheduler()
    {
        var window = TestMainWindowFactory.Create();
        try
        {
            var schedulerField = typeof(NovaTerminal.MainWindow).GetField("_snapshotScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var scheduler = schedulerField.GetValue(window) as SnapshotScheduler;
            Assert.NotNull(scheduler);

            var startedField = typeof(SnapshotScheduler).GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.True((bool)startedField.GetValue(scheduler)!);

            // SetupCommandPalette also runs on every settings-save, not just the first palette
            // open. Invoking it more than once must not create a second scheduler (duplicate
            // FileSystemWatchers on the same directories) nor replace the running one.
            var setupMethod = typeof(NovaTerminal.MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!;
            setupMethod.Invoke(window, null);
            setupMethod.Invoke(window, null);

            Assert.Same(scheduler, schedulerField.GetValue(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Today the three Backup entries can't accumulate because <c>CommandRegistry.Clear()</c> runs
    /// unconditionally at the top of <c>SetupCommandPalette()</c> (pre-existing code, not touched by
    /// this task). Nothing pinned that invariant for the Backup entries specifically, so a future
    /// change that moved a <c>Register</c> call above the <c>Clear()</c>, or made the <c>Clear()</c>
    /// conditional, would silently duplicate them - this asserts the post-condition (exactly three,
    /// with the three expected ids) after several repeated invocations, the way a long session
    /// opening the palette and saving Settings repeatedly actually drives this method.
    /// </summary>
    [AvaloniaFact]
    public void SetupCommandPalette_CalledRepeatedly_DoesNotAccumulateBackupCommands()
    {
        CommandRegistry.Clear();
        var window = TestMainWindowFactory.Create();
        try
        {
            var setupMethod = typeof(NovaTerminal.MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!;

            setupMethod.Invoke(window, null);
            setupMethod.Invoke(window, null);
            setupMethod.Invoke(window, null);

            var backupCommands = CommandRegistry.GetCommands().Where(c => c.Category == "Backup").ToList();

            Assert.Equal(3, backupCommands.Count);
            Assert.Equal(
                new[] { "backup.export", "backup.import", "backup.restore" },
                backupCommands.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClosingTheWindow_DisposesTheSnapshotScheduler_AndClearsTheField()
    {
        var window = TestMainWindowFactory.Create();
        var schedulerField = typeof(NovaTerminal.MainWindow).GetField("_snapshotScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var scheduler = (SnapshotScheduler)schedulerField.GetValue(window)!;
        var disposedField = typeof(SnapshotScheduler).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.False((bool)disposedField.GetValue(scheduler)!);

        window.Close();

        Assert.True((bool)disposedField.GetValue(scheduler)!);
        Assert.Null(schedulerField.GetValue(window));
    }
}
