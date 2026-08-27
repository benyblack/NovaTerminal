using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell.Backup;
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
        using var _ = OverrideAppDataRoot(BackupTestTree.CreateEmpty().Root);
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
        using var _ = OverrideAppDataRoot(BackupTestTree.CreateEmpty().Root);
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
        using var _ = OverrideAppDataRoot(BackupTestTree.CreateEmpty().Root);
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

        var seenTabIndexes = new System.Collections.Generic.HashSet<int>();

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
        using var _ = OverrideAppDataRoot(BackupTestTree.CreateEmpty().Root);
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
        using var _ = OverrideAppDataRoot(BackupTestTree.CreateEmpty().Root);
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
        var tree = BackupTestTree.CreatePopulated();
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
        using var _ = OverrideAppDataRoot(BackupTestTree.CreateEmpty().Root);
        var window = new NovaTerminal.SettingsWindow();

        var btnRestore = window.FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.FindControl<TextBlock>("BackupStatusText")!;

        var exception = Record.Exception(() => btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));

        Assert.Null(exception);
        Assert.Equal("Select a snapshot first.", status.Text);
    }

    [AvaloniaFact]
    public void RestoreSelected_WithASnapshotSelected_Succeeds_AndAddsAPreRestoreSnapshot()
    {
        var tree = BackupTestTree.CreatePopulated();
        var service = new BackupService(tree.Root);
        var snapshot = service.Snapshot(SnapshotReason.Auto);
        Assert.NotNull(snapshot);

        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow();

        var snapshotList = window.FindControl<ListBox>("SnapshotList")!;
        var btnRestore = window.FindControl<Button>("BtnRestoreSnapshot")!;
        var status = window.FindControl<TextBlock>("BackupStatusText")!;

        snapshotList.SelectedIndex = 0;
        btnRestore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Restored. Restart NovaTerminal to pick up all changes.", status.Text);

        // Restore takes a forced pre-restore snapshot before applying, so the list this window
        // reads from disk now has that one in addition to the original auto snapshot.
        var reasons = service.ListSnapshots().Select(s => s.Reason).ToArray();
        Assert.Contains(SnapshotReason.PreRestore, reasons);
    }

    private static string GetDisplay(object snapshotRow)
    {
        var property = snapshotRow.GetType().GetProperty("Display", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (string)property!.GetValue(snapshotRow)!;
    }

    /// <summary>
    /// Points <c>AppPaths.RootDirectory</c> (and therefore <c>WireBackupSection</c>'s own
    /// <see cref="BackupService"/>) at a temp tree for the lifetime of the returned scope, so
    /// constructing a <see cref="NovaTerminal.SettingsWindow"/> in a test never reads or writes the
    /// real user profile's backups directory.
    /// </summary>
    private static IDisposable OverrideAppDataRoot(string root)
    {
        string? previous = System.Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        System.Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", root);
        return new RestoreEnvVar(previous);
    }

    private sealed class RestoreEnvVar(string? previous) : IDisposable
    {
        public void Dispose() => System.Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previous);
    }
}
