using System.Reflection;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell;
using NovaTerminal.Backup;
using NovaTerminal.Tests.Backup;

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

    /// <summary>
    /// F1 (Codex review, PR #362): a successful Import/Restore reloads
    /// <c>SettingsWindow._settings</c>, but <c>MainWindow</c> holds its own separate <c>_settings</c>
    /// instance. Before this fix, closing Settings with Cancel or the window's X took
    /// <c>OpenSettings</c>'s <c>saved == false</c> branch, which never adopted the reload - it kept
    /// the pre-import object. Any later ordinary <c>_settings.Save()</c> (this test uses the "Font:
    /// Increase" palette command, one of roughly ten such call sites) then silently overwrote the
    /// just-imported settings.json with the stale pre-import configuration.
    ///
    /// Drives the real seams: a real <see cref="BackupService.Import"/> (Replace mode), the private
    /// <c>SettingsWindow.ReloadSettingsAfterExternalChangeAsync</c> the Import click handler calls on
    /// success (same reflection seam <c>SettingsWindowBackupSectionTests</c> uses -
    /// <c>Window.ShowDialog</c> hangs this headless host, so the dialog itself is never opened), then
    /// <c>MainWindow.ApplySettingsWindowResult</c> with <c>saved: false</c> - simulating Cancel/X -
    /// pulled out of <c>OpenSettings</c> for exactly this reason. Finally, a real command-palette
    /// invocation of "Font: Increase" stands in for "any ordinary action afterwards".
    /// </summary>
    [AvaloniaFact]
    public async Task ImportReplace_ThenCancelSettings_ThenOrdinarySave_KeepsTheImportedConfiguration()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":77,"ThemeName":"FromImport"}""");
        string bundle = Path.Combine(source.Root, "import.novabackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(target.Root);

        CommandRegistry.Clear();
        var window = TestMainWindowFactory.Create();
        try
        {
            // The dialog is never constructed via `new SettingsWindow(tabIndex, ...)` /
            // `OpenSettings` here (that reaches a real ShowDialog); a parameterless SettingsWindow
            // reads the same overridden root and is exactly what the existing Backup-section tests
            // use for this same reflection seam.
            var settingsWindow = new NovaTerminal.SettingsWindow();

            var importOutcome = new BackupService(target.Root).Import(bundle, ImportMode.Replace);
            Assert.True(importOutcome.Success, importOutcome.Message);

            var reloadMethod = typeof(NovaTerminal.SettingsWindow).GetMethod(
                "ReloadSettingsAfterExternalChangeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(reloadMethod);
            await (Task)reloadMethod!.Invoke(settingsWindow, null)!;
            Assert.True(settingsWindow.ConfigurationReplacedExternally);

            var previewSnapshot = new NovaTerminal.MainWindow.PreviewSnapshot(
                WindowOpacity: 1.0,
                BlurEffect: "Acrylic",
                BackgroundImagePath: "",
                BackgroundImageOpacity: 0.5,
                BackgroundImageStretch: "UniformToFill",
                FontFamily: "Consolas",
                FontSize: 14,
                ThemeName: "Changed");

            // saved: false - the dialog closed via Cancel or the window's X, not Save.
            window.ApplySettingsWindowResult(settingsWindow, saved: false, previewSnapshot);

            var setupMethod = typeof(NovaTerminal.MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!;
            setupMethod.Invoke(window, null);

            var fontIncrease = Assert.Single(CommandRegistry.GetCommands(), c => c.Id == "font_increase");
            fontIncrease.Action(); // _settings.FontSize++; ApplySettingsToAllTabs(); _settings.Save();

            using var afterSave = target.ReadJson("settings.json");
            Assert.Equal("FromImport", afterSave.RootElement.GetProperty("ThemeName").GetString());
            Assert.Equal(78, afterSave.RootElement.GetProperty("FontSize").GetInt32());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Points <c>AppPaths.RootDirectory</c> at a temp tree for the lifetime of the returned scope -
    /// same helper as <c>SettingsWindowBackupSectionTests.OverrideAppDataRoot</c>, duplicated here
    /// per this test suite's existing one-helper-per-file convention.
    /// </summary>
    private static IDisposable OverrideAppDataRoot(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", root);
        return new RestoreEnvVar(previous);
    }

    private sealed class RestoreEnvVar(string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previous);
    }
}
