using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Tests.Backup;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Task 9: <see cref="NovaTerminal.SettingsWindow.SelectBackupPage"/>, the navigation helper the
/// three command-palette "Backup" entries use to land on the Backup &amp; Restore tab.
///
/// The critical property under test is that the index is derived (<c>MainTabs.Items.Count - 1</c>),
/// not hardcoded: a hardcoded "6" would keep compiling and passing today but would silently point
/// at the wrong tab the moment a tab is inserted before Backup - exactly the kind of drift
/// <see cref="SettingsWindowBackupSectionTests"/> already documents happening once, for the SSH tab
/// (PR #332).
/// </summary>
public sealed class SettingsWindowSelectBackupPageTests
{
    [AvaloniaFact]
    public void SelectBackupPage_SelectsTheLastTab_AndItIsTheBackupTab()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow(0);

        var tabs = window.FindControl<TabControl>("MainTabs")!;

        // Start somewhere else, so a no-op SelectBackupPage() implementation can't pass by accident.
        Assert.Equal(0, tabs.SelectedIndex);

        window.SelectBackupPage();

        Assert.Equal(tabs.Items.Count - 1, tabs.SelectedIndex);
        Assert.Equal("Backup", ((TabItem)tabs.Items[tabs.SelectedIndex]!).Header);
    }

    /// <summary>
    /// Drives the same regression from the other direction: proves the index is computed, not
    /// hardcoded, by shrinking the tab strip out from under it and confirming SelectBackupPage
    /// still lands on whatever is now last - a hardcoded "6" would instead select a mid-strip tab
    /// (or throw) once an earlier tab is removed.
    /// </summary>
    [AvaloniaFact]
    public void SelectBackupPage_TracksTabCount_IfATabIsRemovedBeforeBackup()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow(0);

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        int originalCount = tabs.Items.Count;

        // Remove the first tab (Appearance) - Backup is now one slot earlier than its usual index.
        tabs.Items.RemoveAt(0);
        Assert.Equal(originalCount - 1, tabs.Items.Count);

        window.SelectBackupPage();

        Assert.Equal(tabs.Items.Count - 1, tabs.SelectedIndex);
        Assert.Equal("Backup", ((TabItem)tabs.Items[tabs.SelectedIndex]!).Header);
    }

    /// <summary>
    /// SelectBackupPage assigns TabControl.SelectedIndex, the same property the sidebar's own
    /// SelectionChanged wiring listens to (see SettingsWindowBackupSectionTests' regression suite),
    /// so it must sync DataNav rather than leaving the sidebar showing a stale selection.
    /// </summary>
    [AvaloniaFact]
    public void SelectBackupPage_AlsoSelectsTheDataNavSidebarItem()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow(0);

        var connectionNav = window.FindControl<ListBox>("ConnectionNav")!;
        var dataNav = window.FindControl<ListBox>("DataNav")!;

        connectionNav.SelectedIndex = 0;
        Assert.Equal(0, connectionNav.SelectedIndex);

        window.SelectBackupPage();

        Assert.Equal(0, dataNav.SelectedIndex);
        Assert.Equal(-1, connectionNav.SelectedIndex);
    }

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
