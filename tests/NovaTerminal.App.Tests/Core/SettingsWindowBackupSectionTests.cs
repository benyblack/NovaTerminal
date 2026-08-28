using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using NovaTerminal.Backup;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;
using NovaTerminal.Tests.Backup;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Task 8: the Settings window's "Backup &amp; Restore" page (<c>DataNav</c> / the "Backup" tab).
///
/// The critical regression these tests guard: the sidebar <see cref="ListBox"/>es are the real
/// navigation, not the <see cref="TabControl"/> header strip. Each nav's <c>SelectionChanged</c>
/// maps its index onto a tab index with a hardcoded offset (<c>InterfaceNav</c> 0-2,
/// <c>AssistantNav</c> 3-4, <c>ConnectionNav</c> 5, <c>DataNav</c> 6), and
/// <c>SyncSidebarFromTabs</c> maps back. A new tab without both mappings is unreachable and, worse,
/// can silently shift an existing tab's offset onto the wrong sidebar item — exactly what happened
/// to the SSH tab on PR #332 (it stranded "Agent Access"). <see cref="AllNavItems_SelectDistinctTabs_AndSyncRoundTripsCleanly"/>
/// is the test that would have caught that.
/// </summary>
public sealed class SettingsWindowBackupSectionTests
{
    [AvaloniaFact]
    public void BackupTab_NamedControls_AreAllReachableByName()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        Assert.NotNull(window.FindControl<ListBox>("DataNav"));
        Assert.NotNull(window.FindControl<Button>("BtnBackupExport"));
        Assert.NotNull(window.FindControl<Button>("BtnBackupImport"));
        Assert.NotNull(window.FindControl<TextBlock>("BackupStatusText"));
        Assert.NotNull(window.FindControl<ListBox>("SnapshotList"));
        Assert.NotNull(window.FindControl<Button>("BtnRestoreSnapshot"));
    }

    [AvaloniaFact]
    public void BackupTab_IsLastTab_AndDataNavHasOneItem()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        var dataNav = window.FindControl<ListBox>("DataNav")!;

        // 7 tabs total (Appearance, Profiles, Shortcuts, Command Assist, Agent Access, SSH,
        // Backup) — Backup at index 6, the END of the strip, so the pre-existing offsets
        // (InterfaceNav 0-2, AssistantNav 3-4, ConnectionNav 5) stay true.
        Assert.Equal(7, tabs.Items.Count);
        Assert.Equal("Backup", ((TabItem)tabs.Items[6]!).Header);
        Assert.Single(dataNav.Items);
    }

    /// <summary>
    /// The regression test called out in the task brief: walks every sidebar item across all four
    /// nav groups (InterfaceNav, AssistantNav, ConnectionNav, DataNav), asserting each one selects
    /// its own distinct tab index (no two sidebar items collide on the same tab, and none map
    /// outside 0..6), and that after each selection, <c>SyncSidebarFromTabs</c> — invoked through
    /// the live <c>TabControl.SelectionChanged</c> wiring, exactly as a real click would drive it —
    /// leaves that one sidebar item selected and clears the other three. This is the exact failure
    /// mode from PR #332 (Codex review finding): the SSH tab shipped without a sidebar item and a
    /// mapping in both directions, which silently remapped "Agent Access" onto SSH's tab index.
    /// </summary>
    [AvaloniaFact]
    public void AllNavItems_SelectDistinctTabs_AndSyncRoundTripsCleanly()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        var interfaceNav = window.FindControl<ListBox>("InterfaceNav")!;
        var assistantNav = window.FindControl<ListBox>("AssistantNav")!;
        var connectionNav = window.FindControl<ListBox>("ConnectionNav")!;
        var dataNav = window.FindControl<ListBox>("DataNav")!;

        var allNavs = new[] { interfaceNav, assistantNav, connectionNav, dataNav };

        // Sanity on per-group item counts first: a silent add/drop here would otherwise make the
        // loop below under- or over-cover the sidebar without failing anything by itself.
        Assert.Equal(3, interfaceNav.Items.Count);
        Assert.Equal(2, assistantNav.Items.Count);
        Assert.Equal(1, connectionNav.Items.Count);
        Assert.Equal(1, dataNav.Items.Count);

        var seenTabIndexes = new HashSet<int>();

        foreach (var nav in allNavs)
        {
            for (int i = 0; i < nav.Items.Count; i++)
            {
                // Clear every group first so the selection below is unambiguous — otherwise a
                // stale selection left over from a previous iteration could make a broken mapping
                // look correct by accident.
                foreach (var other in allNavs) other.SelectedIndex = -1;

                nav.SelectedIndex = i;

                int tabIndex = tabs.SelectedIndex;
                Assert.InRange(tabIndex, 0, 6);
                Assert.True(
                    seenTabIndexes.Add(tabIndex),
                    $"Tab index {tabIndex} was already claimed by a different sidebar item — the offsets collide (this is the PR #332 failure mode).");

                // Round trip: SyncSidebarFromTabs (fired via TabControl.SelectionChanged, same as
                // a real click) must map this tab index back to exactly this nav item, with the
                // other three groups cleared.
                foreach (var other in allNavs)
                {
                    if (ReferenceEquals(other, nav))
                    {
                        Assert.Equal(i, other.SelectedIndex);
                    }
                    else
                    {
                        Assert.Equal(-1, other.SelectedIndex);
                    }
                }
            }
        }

        // Every one of the 7 tabs was reached by exactly one sidebar item.
        Assert.Equal(7, seenTabIndexes.Count);
    }

    /// <summary>
    /// Same regression, driven from the other direction: setting <c>TabControl.SelectedIndex</c>
    /// directly (as the <c>initialTab</c> constructor parameter and other non-sidebar callers do)
    /// must still sync back to exactly one sidebar item per index, with the rest cleared.
    /// </summary>
    [AvaloniaFact]
    public void SyncSidebarFromTabs_EveryTabIndex_SelectsExactlyOneSidebarItem()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        var interfaceNav = window.FindControl<ListBox>("InterfaceNav")!;
        var assistantNav = window.FindControl<ListBox>("AssistantNav")!;
        var connectionNav = window.FindControl<ListBox>("ConnectionNav")!;
        var dataNav = window.FindControl<ListBox>("DataNav")!;

        for (int idx = 0; idx < 7; idx++)
        {
            tabs.SelectedIndex = idx;

            var (expectedNav, expectedIndex) = idx switch
            {
                < 3 => (interfaceNav, idx),
                < 5 => (assistantNav, idx - 3),
                < 6 => (connectionNav, idx - 5),
                _ => (dataNav, idx - 6)
            };

            foreach (var nav in new[] { interfaceNav, assistantNav, connectionNav, dataNav })
            {
                Assert.Equal(ReferenceEquals(nav, expectedNav) ? expectedIndex : -1, nav.SelectedIndex);
            }
        }
    }

    [AvaloniaFact]
    public void ClickingDataNav_SelectsTheBackupTab_AndOtherSidebarItemsStillSelectTheirOwnPage()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        var dataNav = window.FindControl<ListBox>("DataNav")!;
        var connectionNav = window.FindControl<ListBox>("ConnectionNav")!;

        dataNav.SelectedIndex = 0;
        Assert.Equal(6, tabs.SelectedIndex);

        // The regression this guards directly: clicking a pre-existing item (SSH, the previous
        // last tab) must still land on SSH's own tab, not get remapped onto Backup.
        connectionNav.SelectedIndex = 0;
        Assert.Equal(5, tabs.SelectedIndex);
    }

    [AvaloniaFact]
    public void SnapshotList_IsPopulatedFromDisk_WithReasonAndSizeInTheDisplayText()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var written = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(written);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var snapshotList = window.FindControl<ListBox>("SnapshotList")!;
        var rows = snapshotList.ItemsSource!.Cast<object>().ToArray();

        Assert.Single(rows);
        string display = GetDisplay(rows[0]);
        Assert.Contains("automatic", display);
    }

    [AvaloniaFact]
    public void RestoreSelected_WithNoSelection_ShowsErrorStatus_AndDoesNotThrow()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var btnRestore = window.FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.FindControl<TextBlock>("BackupStatusText")!;

        // Safe to click: with nothing selected the handler returns before it ever reaches the
        // confirmation dialog, so this can't hit the ShowDialog hang described below.
        var exception = Record.Exception(() => btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));

        Assert.Null(exception);
        Assert.Equal("Select a snapshot first.", status.Text);
    }

    /// <summary>
    /// Fix round 1 (Important finding, design call): Restore now asks for confirmation before
    /// overwriting live configuration, naming the snapshot's timestamp and reason, stating that it
    /// replaces the categories the snapshot contains, and noting that a pre-restore snapshot is
    /// taken first. This tests that wording directly, via the private
    /// <c>BuildRestoreConfirmationText</c> helper the confirmation dialog is built from.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT click "Restore selected" with a snapshot selected and drive the
    /// confirmation dialog end to end. <c>MainWindowShellExitTests</c> already found, empirically,
    /// that a real <c>Window.ShowDialog</c> with no owner ever shown and no button for anything to
    /// click does not return in this repo's headless test host — the UI thread gets stuck inside
    /// <c>ShowDialog</c> itself, not merely at the awaiting call site, so even a fire-and-forget
    /// click risks hanging the test run. <c>BuildRestoreConfirmationText</c> exists specifically so
    /// the wording requirement can be verified without going anywhere near that call.
    /// </remarks>
    [AvaloniaFact]
    public void RestoreConfirmationText_NamesTheSnapshotAndReason_AndExplainsReplaceAndPreRestoreUndo()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var written = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(written);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var snapshotList = window.FindControl<ListBox>("SnapshotList")!;
        object row = snapshotList.ItemsSource!.Cast<object>().Single();

        var method = typeof(NovaTerminal.SettingsWindow).GetMethod(
            "BuildRestoreConfirmationText", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object result = method!.Invoke(null, new[] { row })!;
        var (headline, body) = ((string Headline, string Body))result;

        // Names the snapshot: its timestamp and reason.
        Assert.Contains(written!.CreatedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), headline);
        Assert.Contains("automatic", headline, StringComparison.OrdinalIgnoreCase);

        // States it replaces the categories the snapshot contains, and that a pre-restore
        // snapshot is taken first so the restore itself can be undone.
        Assert.Contains("replaces", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("taken first", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("undone", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fix round 2 (Important finding): the confirmation gate added in round 1 for
    /// <c>BuildRestoreConfirmationText</c>'s wording left the actual wiring uncovered - does
    /// confirming really trigger the restore? Pins the "confirmed" branch through
    /// <c>RestoreConfirmationOverride</c>, the injectable seam <c>WireBackupSection</c>'s Restore
    /// click handler falls back from (production always uses the real, untestable
    /// <c>ConfirmRestoreAsync</c>/<c>ShowDialog</c>). Verifies the tracked file actually rolls back
    /// on disk (not just that some outcome was reported) and that a PreRestore snapshot appears.
    /// </summary>
    [AvaloniaFact]
    public void RestoreSelected_WhenConfirmationReturnsTrue_CallsRestore_AndRollsBackTheTrackedFile()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        // Drift after the snapshot, so restoring has something real to roll back - proves
        // service.Restore actually ran, not just that some status text changed.
        tree.WriteFile("settings.json", """{"FontSize":99,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow { RestoreConfirmationOverride = _ => Task.FromResult(true) };

        var snapshotList = window.FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.FindControl<TextBlock>("BackupStatusText")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // M2: the status text now surfaces BackupService's own outcome message (which is why it
        // says "Restored", not "Imported" - M1's operation-noun fix - and carries the
        // credentials note, since the populated tree's ssh/profiles.json makes Connections one
        // of the restored categories) rather than a generic, hand-composed string.
        Assert.Equal(
            "Restored 6 categories (Replace). Connection passwords are not included in a bundle " +
            "— re-enter them on first connect. Restart NovaTerminal to pick up all changes.",
            status.Text);

        // The tracked file rolled back to the snapshot's content on disk.
        using var restoredSettings = tree.ReadJson("settings.json");
        Assert.Equal(14, restoredSettings.RootElement.GetProperty("FontSize").GetInt32());

        // Restore forces a pre-restore snapshot before applying.
        var reasons = service.ListSnapshots().Select(s => s.Reason).ToArray();
        Assert.Contains(SnapshotReason.PreRestore, reasons);
    }

    /// <summary>
    /// Fix round 2 (Important finding): the other half of the same gap - does declining actually
    /// prevent the restore? This is the assertion that matters most: proving no PreRestore
    /// snapshot appears is what rules out Cancel being a no-op that silently restores anyway.
    /// </summary>
    [AvaloniaFact]
    public void RestoreSelected_WhenConfirmationReturnsFalse_DoesNotCallRestore_AndAddsNoPreRestoreSnapshot()
    {
        using var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        tree.WriteFile("settings.json", """{"FontSize":99,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow { RestoreConfirmationOverride = _ => Task.FromResult(false) };

        var snapshotList = window.FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.FindControl<TextBlock>("BackupStatusText")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // Declining is a genuine no-op: the handler returns before SetStatus, so the status text
        // set at construction (empty) is untouched.
        Assert.Equal(string.Empty, status.Text);

        // The tracked file was never touched.
        using var currentSettings = tree.ReadJson("settings.json");
        Assert.Equal(99, currentSettings.RootElement.GetProperty("FontSize").GetInt32());

        // The assertion that matters: no PreRestore snapshot was written, proving Restore itself
        // never ran - Cancel is not a no-op that silently restores anyway.
        var reasons = service.ListSnapshots().Select(s => s.Reason).ToArray();
        Assert.DoesNotContain(SnapshotReason.PreRestore, reasons);
        Assert.Single(reasons);
    }

    /// <summary>
    /// I1 (final whole-branch review, Important, user-visible data loss): SettingsWindow loads
    /// <c>_settings</c> once at construction. Restore changes settings.json on disk directly, but
    /// before this fix nothing told the already-open window - clicking Save afterward called
    /// <c>_settings.Save()</c> with the pre-restore snapshot still in memory, silently reverting
    /// the restore that had just completed and telling the user the opposite ("Restart
    /// NovaTerminal to pick up all changes"). Fully click-driven (real BtnRestoreSnapshot and
    /// BtnSave clicks, via the same <see cref="SettingsWindow.RestoreConfirmationOverride"/> seam
    /// the other Restore tests use) rather than reflecting into a private method, since Restore's
    /// only modal (the confirmation dialog) already has a test seam - unlike Import, which needs
    /// a file picker AND a mode-selection dialog neither of which can run headlessly (see
    /// <see cref="Import_ThenSave_DoesNotRevertTheImportedSettings"/> below for that path, driven
    /// directly instead).
    /// </summary>
    [AvaloniaFact]
    public void RestoreSelected_ThenSave_DoesNotRevertTheRestoredSettings()
    {
        using var tree = BackupTestTree.CreatePopulated();
        tree.WriteFile("settings.json", """{"FontSize":77,"ThemeName":"FromSnapshot"}""");
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        // Drift after the snapshot: this is both what Restore rolls back AND what the already-open
        // window's own _settings (loaded at construction, before this write) reflects.
        tree.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow { RestoreConfirmationOverride = _ => Task.FromResult(true) };

        var snapshotList = window.FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.FindControl<Button>("BtnRestoreSnapshot")!;
        var btnSave = window.FindControl<Button>("BtnSave")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // Sanity: the restore itself worked (already covered elsewhere; repeated here so a
        // failure below points at Save, not at Restore).
        using (var restored = tree.ReadJson("settings.json"))
        {
            Assert.Equal(77, restored.RootElement.GetProperty("FontSize").GetInt32());
        }

        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = tree.ReadJson("settings.json");
        Assert.Equal(77, afterSave.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("FromSnapshot", afterSave.RootElement.GetProperty("ThemeName").GetString());
    }

    /// <summary>
    /// The same I1 bug, for the scenario the review actually names (Import, Replace mode).
    /// Import's own click handler needs a real file picker AND a mode-selection dialog - neither
    /// drivable headlessly (see <see cref="ConfirmRestoreAsync"/>'s remarks on why a bare
    /// <c>Window.ShowDialog</c> hangs this host) - and unlike Restore's confirmation, no test seam
    /// exists for either. Per the plan's own guidance, this drives the underlying method chain
    /// directly instead: a real <see cref="BackupService.Import"/> call, then the private
    /// <c>ReloadSettingsAfterExternalChangeAsync</c> the click handler calls on success (reached via
    /// reflection, the same established pattern <c>BuildRestoreConfirmationText</c> uses
    /// elsewhere in this file), then a real BtnSave click.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_ThenSave_DoesNotRevertTheImportedSettings()
    {
        using var source = BackupTestTree.CreatePopulated();
        source.WriteFile("settings.json", """{"FontSize":77,"ThemeName":"FromImport"}""");
        string bundle = Path.Combine(source.Root, "import.novabackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        target.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Changed"}""");

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new NovaTerminal.SettingsWindow();

        var targetService = new BackupService(target.Root);
        var outcome = targetService.Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = target.ReadJson("settings.json");
        Assert.Equal(77, afterSave.RootElement.GetProperty("FontSize").GetInt32());
        Assert.Equal("FromImport", afterSave.RootElement.GetProperty("ThemeName").GetString());
    }

    /// <summary>
    /// I1 residual, Fix 1 (scoped re-review): <c>PopulateThemes</c> fills the theme combo boxes
    /// from disk once, at construction. <c>ReloadSettingsAfterExternalChangeAsync</c> did not re-run it,
    /// so an import bringing a NEW theme file plus a settings.json naming it left the combo holding
    /// the pre-import theme list — no entry for the imported theme. <c>LoadCurrentSettings</c>'s
    /// theme-selection loop then had nothing to select, left <c>SelectedItem</c> on the stale
    /// pre-import theme, and <c>SaveAndClose</c> would write that stale name back — the original I1
    /// bug, narrowed to one field.
    ///
    /// The pre-state <c>ThemeName</c> here is "Default", deliberately present in the combo both
    /// before and after the import (<c>ThemeManager</c> always synthesizes "Default"). The other
    /// tests in this file use "Changed" as a pre-state, which is absent from every combo — that
    /// leaves <c>SelectedItem</c> null and the `is ComboBoxItem` guard at <c>SaveAndClose</c> skips
    /// the write entirely, which would silently miss this bug. A present pre-state name is what
    /// makes the stale selection observable at Save.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_BringingNewTheme_ThenSave_PersistsTheImportedThemeName()
    {
        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Imported"}""");
        source.WriteFile(Path.Combine("themes", "imported.json"), """{"name":"Imported"}""");
        string bundle = Path.Combine(source.Root, "import.novabackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        // The populated target tree's settings.json already names ThemeName "Default" — present
        // in the combo both before and after the import (see remarks above).
        using var target = BackupTestTree.CreatePopulated();

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new NovaTerminal.SettingsWindow();

        var targetService = new BackupService(target.Root);
        var outcome = targetService.Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = target.ReadJson("settings.json");
        Assert.Equal("Imported", afterSave.RootElement.GetProperty("ThemeName").GetString());
    }

    /// <summary>
    /// Finding 1 (Codex review round 2, PR #362): the exact same residual as
    /// <see cref="Import_BringingNewTheme_ThenSave_PersistsTheImportedThemeName"/> above, for
    /// fonts. <c>PopulateFonts</c> builds its list via
    /// <c>BuildFontFamilyChoices(..., _selectedProfile?.FontFamily ?? _settings.FontFamily)</c> —
    /// it explicitly seeds the configured font even when it is not installed locally. Before the
    /// fix, <c>ReloadSettingsAfterExternalChangeAsync</c> called <c>PopulateThemes</c> but not
    /// <c>PopulateFonts</c>, so a bundle imported from another machine naming a font absent here
    /// left <c>FontList</c> holding the pre-import choices; <c>LoadCurrentSettings</c> could not
    /// select the imported font, and a later Save wrote the stale font back over it.
    ///
    /// The target tree's pre-import <c>FontFamily</c> is left unset, defaulting to
    /// <c>BundledFontCatalog.DefaultTerminalFontFamily</c> — deliberately present in
    /// <c>FontList</c> both before and after the import (<c>PopulateFonts</c> always seeds the
    /// bundled default), exactly the "present pre-state name" precedent the theme test above
    /// documents: an absent pre-state would leave <c>SelectedItem</c> null and the `is
    /// ComboBoxItem` guard at <c>SaveAndClose</c> would skip the write entirely, silently missing
    /// this bug.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_BringingFontNotInstalledLocally_ThenSave_PersistsTheImportedFontFamily()
    {
        const string importedFont = "TotallyNotARealFont-XYZ123";

        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", $$"""{"FontSize":14,"ThemeName":"Default","FontFamily":"{{importedFont}}"}""");
        string bundle = Path.Combine(source.Root, "import.novabackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        // The populated target tree's settings.json does not set FontFamily, so
        // TerminalSettings.Load() defaults it to the bundled default font — present in FontList
        // both before and after the import (see remarks above).
        using var target = BackupTestTree.CreatePopulated();

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new NovaTerminal.SettingsWindow();

        var targetService = new BackupService(target.Root);
        var outcome = targetService.Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = target.ReadJson("settings.json");
        Assert.Equal(importedFont, afterSave.RootElement.GetProperty("FontFamily").GetString());
    }

    /// <summary>
    /// Sibling of <see cref="Import_BringingFontNotInstalledLocally_ThenSave_PersistsTheImportedFontFamily"/>
    /// and <see cref="Import_BringingNewTheme_ThenSave_PersistsTheImportedThemeName"/>, for the one
    /// populate path the reload did not cover: the Command Assist snippet list.
    ///
    /// Every other <c>Populate*</c> is called straight from the constructor, so
    /// <c>ReloadSettingsAfterExternalChangeAsync</c> could re-run it by simply repeating the constructor's
    /// sequence. <c>PopulateCommandAssistSnippetsPanel</c> is different — it is reached through
    /// <c>WireCommandAssistSnippetsRow</c>'s <c>Opened += async (_, _) =&gt; await
    /// ReloadCommandAssistSnippetsAsync()</c> handler, because the store arrives by property
    /// assignment after the constructor has run. <c>Opened</c> fires once, the first time the window
    /// is shown; the reload runs on an ALREADY-OPEN window and does not reopen it, so before this fix
    /// nothing refreshed the snippet rows. An import whose bundle carries a different
    /// <c>command-assist/snippets.json</c> (which <c>BackupService</c> replaces wholesale in BOTH
    /// modes) left the visible list showing the pre-import snippets until the window was closed and
    /// reopened.
    ///
    /// Unlike the theme/font siblings, the observable damage is not at Save — snippets do not live in
    /// settings.json and the Save button never writes them — it is the stale rows themselves, which
    /// the user can then edit or delete, acting on a list that no longer describes the file. So this
    /// asserts on the panel's contents directly.
    /// </summary>
    [AvaloniaFact]
    public async Task Import_BringingNewSnippets_RefreshesTheAlreadyOpenSnippetsPanel()
    {
        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Default"}""");
        await new JsonSnippetStore(SnippetsPathIn(source))
            .UpsertAsync(NewSnippet("imported-1", "Imported snippet", "echo imported"));
        string bundle = Path.Combine(source.Root, "import.novabackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        // The live store MainWindow.OpenSettings injects — a real JsonSnippetStore over the target
        // tree's own file, so the import below genuinely changes what it reads.
        var targetStore = new JsonSnippetStore(SnippetsPathIn(target));
        await targetStore.UpsertAsync(NewSnippet("local-1", "Local snippet", "echo local"));

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new NovaTerminal.SettingsWindow { CommandAssistSnippetStore = targetStore };

        // Stands in for the one-shot Opened handler: the window is open and its snippet list has
        // already been populated from the pre-import file.
        await InvokeReloadCommandAssistSnippetsAsync(window);
        Assert.Equal(new[] { "Local snippet" }, SnippetRowNames(window));

        var outcome = new BackupService(target.Root).Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        Assert.Equal(new[] { "Imported snippet" }, SnippetRowNames(window));
    }

    /// <summary>
    /// Codex review (PR #364, P2): refreshing the Settings snippet panel is only half of what a
    /// snippet change owes the app. <c>MainWindow.OpenSettings</c> subscribes
    /// <c>DismissCommandAssistSurfaces</c> to <c>OnCommandAssistSnippetsChanged</c>, and the add,
    /// edit and delete paths all raise it - the rows an open assist bubble or popup is showing are a
    /// snapshot of a ranking pass, and nothing else invalidates them (see that method's own remarks,
    /// written for the identical "Clear history" case).
    ///
    /// An import or restore is the most destructive snippet change there is: <c>BackupService</c>
    /// replaces <c>snippets.json</c> wholesale in BOTH modes, so every snippet the user had can
    /// vanish at once. Without this notification a popup left open behind the Settings dialog goes on
    /// displaying - and accepting - snippets the import just deleted, until some later suggestion
    /// refresh happens to rebuild it.
    ///
    /// Asserts on the event rather than on pane state because the event IS the seam MainWindow wires
    /// to; a pane-level assertion would need a whole MainWindow and would still be testing this same
    /// handoff one indirection later.
    /// </summary>
    [AvaloniaFact]
    public async Task ReloadAfterExternalChange_NotifiesTheHostThatSnippetsChanged()
    {
        // A bundle with no snippets at all: the import removes the local one outright, which is the
        // case where stale rows in an open popup are actively wrong rather than merely out of date.
        using var source = BackupTestTree.CreateEmpty();
        source.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Default"}""");
        source.WriteFile(Path.Combine("command-assist", "snippets.json"), "[]");
        string bundle = Path.Combine(source.Root, "import.novabackup");
        Assert.True(new BackupService(source.Root).Export(bundle).Success);

        using var target = BackupTestTree.CreatePopulated();
        var targetStore = new JsonSnippetStore(SnippetsPathIn(target));
        await targetStore.UpsertAsync(NewSnippet("local-1", "Local snippet", "echo local"));

        using var _ = OverrideAppDataRoot(target.Root);
        var window = new NovaTerminal.SettingsWindow { CommandAssistSnippetStore = targetStore };

        int notifications = 0;
        window.OnCommandAssistSnippetsChanged += () => notifications++;

        await InvokeReloadCommandAssistSnippetsAsync(window);
        Assert.Equal(new[] { "Local snippet" }, SnippetRowNames(window));

        var outcome = new BackupService(target.Root).Import(bundle, ImportMode.Replace);
        Assert.True(outcome.Success, outcome.Message);

        await InvokeReloadAfterExternalChangeAsync(window);

        // The panel emptying is the precondition; the notification is the point.
        Assert.Empty(SnippetRowNames(window));
        Assert.Equal(1, notifications);
    }

    private static string SnippetsPathIn(BackupTestTree tree)
        => Path.Combine(tree.Root, "command-assist", "snippets.json");

    private static CommandSnippet NewSnippet(string id, string name, string command) => new(
        Id: id,
        Name: name,
        CommandText: command,
        Description: null,
        ShellKind: null,
        WorkingDirectory: null,
        IsPinned: true,
        CreatedAt: DateTimeOffset.UnixEpoch,
        LastUsedAt: null);

    /// <summary>
    /// The names shown by the rows currently in <c>CommandAssistSnippetsPanel</c> — the first
    /// <see cref="TextBox"/> of each row, which <c>CreateCommandAssistSnippetRow</c> seeds with the
    /// snippet's name. Reading the built controls rather than the editor's list is the point: the bug
    /// is precisely that the two can disagree. The empty-list placeholder is not a row and is skipped.
    /// </summary>
    private static string[] SnippetRowNames(NovaTerminal.SettingsWindow window)
    {
        var panel = window.FindControl<StackPanel>("CommandAssistSnippetsPanel")!;

        // FirstOrDefault, not First: an empty snippet list is a legitimate panel state, and
        // PopulateCommandAssistSnippetsPanel renders it as a single explanatory TextBlock with no
        // TextBox in it. Only children that actually carry an editor are snippet rows.
        return panel.Children
            .Select(row => row.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault())
            .Where(nameEditor => nameEditor != null)
            .Select(nameEditor => nameEditor!.Text ?? "")
            .ToArray();
    }

    private static async Task InvokeReloadCommandAssistSnippetsAsync(NovaTerminal.SettingsWindow window)
    {
        var method = typeof(NovaTerminal.SettingsWindow).GetMethod(
            "ReloadCommandAssistSnippetsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(window, null)!;
    }

    /// <summary>
    /// Drives the private <c>ReloadSettingsAfterExternalChangeAsync</c> the Import and Restore click
    /// handlers call on success. Reflection, and awaited: the method became a <see cref="Task"/> when
    /// the Command Assist snippet reload (genuinely async - it re-reads the store's file) joined the
    /// synchronous repopulation, so invoking without awaiting would race the assertions that follow.
    /// </summary>
    private static async Task InvokeReloadAfterExternalChangeAsync(NovaTerminal.SettingsWindow window)
    {
        var method = typeof(NovaTerminal.SettingsWindow).GetMethod(
            "ReloadSettingsAfterExternalChangeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(window, null)!;
    }


    /// <summary>
    /// Finding 2 (Codex review round 2, PR #362): the Merge/Replace import prompt says Merge
    /// "keeps items you have locally that the bundle does not contain." That is false for
    /// Snippets — <c>BackupService.BuildPlan</c> replaces <c>snippets.json</c> wholesale in BOTH
    /// modes (a deliberate, spec'd design decision: the file is a flat array with no stable id, so
    /// there is nothing to merge by — see <c>BackupImportTests.Snippets_AlwaysReplacedWholesale</c>).
    /// A user choosing Merge specifically to keep local snippets would lose them with no warning.
    /// Reaches the wording directly via the private <c>BuildImportModeBodyText</c> helper — the
    /// same reflection-free-body/reflection-only-for-the-modal-itself split
    /// <c>RestoreConfirmationText_...</c> above uses for <c>BuildRestoreConfirmationText</c> —
    /// since a real <c>PromptForImportModeAsync</c>'s <c>ShowDialog</c> hangs this repo's headless
    /// host (see that test's remarks).
    /// </summary>
    [Fact]
    public void ImportModeBodyText_MentionsSnippetsReplacedWholesale_OnlyWhenBundleContainsSnippets()
    {
        var method = typeof(NovaTerminal.SettingsWindow).GetMethod(
            "BuildImportModeBodyText", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var withSnippets = new BundleInspection(
            NewManifestForBodyTextTest(),
            new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Settings] = 1,
                [BackupCategory.Snippets] = 1,
            });

        var withoutSnippets = new BundleInspection(
            NewManifestForBodyTextTest(),
            new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Settings] = 1,
                [BackupCategory.Snippets] = 0,
            });

        string withSnippetsText = (string)method!.Invoke(null, new object[] { withSnippets })!;
        string withoutSnippetsText = (string)method.Invoke(null, new object[] { withoutSnippets })!;

        Assert.Contains("replaced entirely", withSnippetsText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replaced entirely", withoutSnippetsText, StringComparison.OrdinalIgnoreCase);

        // The base guarantee must still hold for a bundle without Snippets — this is a copy fix,
        // not a semantics change.
        Assert.Contains("keeps items you have locally", withoutSnippetsText, StringComparison.OrdinalIgnoreCase);
    }

    private static BackupManifest NewManifestForBodyTextTest() => new()
    {
        SchemaVersion = BackupManifest.CurrentSchemaVersion,
        AppVersion = "1.0.0-test",
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Machine = "TEST",
        Categories = new[] { "settings", "snippets" }
    };

    /// <summary>
    /// I1 residual, Fix 2 (scoped re-review): <c>_selectedProfile</c> points at an entry of the
    /// PRE-reload <c>_profilesList</c>. <c>ReloadSettingsAfterExternalChangeAsync</c> rebuilds
    /// <c>_profilesList</c> with fresh <c>TerminalProfile</c> instances (freshly deserialized from
    /// the reloaded settings.json) but, before this fix, never re-pointed <c>_selectedProfile</c> —
    /// it was left dangling, referencing an object no longer reachable from <c>_profilesList</c>.
    /// The profile editor's KeyUp handlers are bound to <c>_selectedProfile</c> directly, so an
    /// edit typed right after an import/restore would silently mutate the detached object and be
    /// dropped at Save instead of landing on the profile actually in the list.
    ///
    /// Selects the one profile by a real ListBox selection (which drives the same
    /// SelectionChanged → SwitchSelectedProfile path a user's click would), reloads (simulating
    /// what Import/Restore's success handler does), types a name edit via a real KeyUp on the
    /// name TextBox (exactly how ProfileNameInput's handler is wired), then Saves — proving the
    /// edit landed on the live post-reload profile object, not a detached pre-reload one.
    /// </summary>
    [AvaloniaFact]
    public async Task ReloadAfterExternalChange_ThenEditSelectedProfile_ThenSave_PersistsTheEdit()
    {
        using var tree = BackupTestTree.CreateEmpty();
        const string profileId = "11111111-1111-1111-1111-111111111111";
        tree.WriteFile(
            "settings.json",
            $$"""
            {"FontSize":14,"ThemeName":"Default","DefaultProfileId":"{{profileId}}",
             "Profiles":[{"Id":"{{profileId}}","Name":"Original","Command":"{{ProfileCommandForThisOs}}"}]}
            """);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var profilesListBox = window.FindControl<ListBox>("ProfilesListBox")!;
        Assert.Single(profilesListBox.Items);
        profilesListBox.SelectedIndex = 0;

        // The external change: same profile Id (as a real Import/Restore preserves), but a
        // machine-local field like FontSize also drifted, standing in for whatever else an
        // import brought along.
        tree.WriteFile(
            "settings.json",
            $$"""
            {"FontSize":18,"ThemeName":"Default","DefaultProfileId":"{{profileId}}",
             "Profiles":[{"Id":"{{profileId}}","Name":"Original","Command":"{{ProfileCommandForThisOs}}"}]}
            """);

        await InvokeReloadAfterExternalChangeAsync(window);

        // Edit the profile's name exactly as a user typing after the reload would: set the
        // TextBox's Text, then raise the real KeyUp event ProfileNameInput's handler listens for.
        var nameInput = window.FindControl<TextBox>("ProfileNameInput")!;
        nameInput.Text = "EditedAfterReload";
        nameInput.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = Key.D,
            Source = nameInput,
        });

        var btnSave = window.FindControl<Button>("BtnSave")!;
        btnSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        using var afterSave = tree.ReadJson("settings.json");
        var savedProfiles = afterSave.RootElement.GetProperty("Profiles");
        Assert.Equal(1, savedProfiles.GetArrayLength());
        Assert.Equal("EditedAfterReload", savedProfiles[0].GetProperty("Name").GetString());
    }

    /// <summary>
    /// Fix round 1 (Important finding): the "passwords are not included, re-enter them" copy is
    /// the user-facing half of a guarantee the whole feature is built around. Nothing asserted on
    /// it before this - a future rewording or accidental deletion would still pass every other
    /// test in this file.
    /// </summary>
    [AvaloniaFact]
    public void BackupTab_StatesPasswordsAreNotIncluded_AndMustBeReEntered()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        // The logical tree (not the visual tree) is used here deliberately: the Backup tab's
        // content is a fully-built control graph the moment the AXAML loader constructs
        // TabItem.Content, but its VISUAL tree only materializes once TabControl actually
        // presents that tab (template-driven, lazy) - which never happens here since this window
        // is never shown or selected onto tab 6.
        string? passwordCopy = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .FirstOrDefault(text =>
                text is not null &&
                text.Contains("never included", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("re-enter", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(passwordCopy);
    }

    /// <summary>
    /// Fix round 1 (Important finding): <c>WireBackupSection</c> runs unconditionally from the
    /// constructor with no try/catch around <c>ListSnapshots()</c>, so a locked or inaccessible
    /// backups directory used to propagate an exception straight out of the constructor - the user
    /// could not open Settings at all. Proves the window still constructs, falls back to an empty
    /// snapshot list, and surfaces the failure through the status text instead.
    /// </summary>
    /// <remarks>
    /// The restriction is applied and then verified by actually trying the enumeration (mirroring
    /// <c>RemoteInstallerIntegrationTests</c>' "decide the skip by trying it rather than guessing
    /// the platform") - an elevated or root process can ignore a deny ACL / zeroed Unix mode
    /// entirely, in which case this skips rather than asserting a false negative.
    /// </remarks>
    [AvaloniaFact]
    public void Constructing_WhenSnapshotEnumerationFails_StillOpens_WithAnEmptyListAndAStatusMessage()
    {
        using var tree = BackupTestTree.CreateEmpty();
        string backupsDirectory = Path.Combine(tree.Root, "backups");
        Directory.CreateDirectory(backupsDirectory);
        File.WriteAllText(
            Path.Combine(backupsDirectory, "auto-20260101T000000Z-abc0000000000000.novabackup"),
            "not a real bundle - enumeration must fail before this is ever opened");

        bool blocked = TryBlockDirectoryListing(backupsDirectory, out Action restore);
        try
        {
            if (!blocked)
            {
                Assert.Skip("this process can enumerate a directory it just denied itself access to (root, or an unrestricted account)");
            }

            using var _ = OverrideAppDataRoot(tree.Root);

            NovaTerminal.SettingsWindow? window = null;
            var exception = Record.Exception(() => window = new NovaTerminal.SettingsWindow());

            Assert.Null(exception);
            Assert.NotNull(window);

            var snapshotList = window!.FindControl<ListBox>("SnapshotList")!;
            var status = window.FindControl<TextBlock>("BackupStatusText")!;

            Assert.Empty(snapshotList.ItemsSource!.Cast<object>());
            Assert.Contains("snapshot", status.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            restore();
        }
    }

    /// <summary>
    /// A command TerminalSettings.Load() recognizes as a native shell for the OS running the
    /// test. Without this, Load()'s cross-platform polish (a Local profile whose command targets
    /// another OS doesn't count as a "native shell found") would silently pad the loaded profile
    /// list with the current OS's default shells, breaking the count assertion in
    /// <see cref="ReloadAfterExternalChange_ThenEditSelectedProfile_ThenSave_PersistsTheEdit"/>.
    /// </summary>
    private static string ProfileCommandForThisOs => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

    private static string GetDisplay(object snapshotRow)
    {
        var property = snapshotRow.GetType().GetProperty("Display", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (string)property!.GetValue(snapshotRow)!;
    }

    /// <summary>
    /// Denies directory-listing access to <paramref name="directory"/> for the current process,
    /// verifying - rather than assuming - that this actually blocks <c>Directory.GetFiles</c>
    /// before reporting success. The returned <paramref name="restore"/> action always undoes the
    /// change, whether or not the block took, so temp-directory cleanup can proceed either way.
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
            Directory.GetFiles(directory, "*.novabackup");
            return false; // enumeration still succeeded - the restriction did not take (root, etc.)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Points <c>AppPaths.RootDirectory</c> (and therefore <c>WireBackupSection</c>'s own
    /// <see cref="BackupService"/>) at a temp tree for the lifetime of the returned scope, so
    /// constructing a <see cref="NovaTerminal.SettingsWindow"/> in a test never reads or writes the
    /// real user profile's backups directory.
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
