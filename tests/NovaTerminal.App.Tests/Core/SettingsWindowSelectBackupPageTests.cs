using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Tests.Backup;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Task 9: <see cref="NovaTerminal.SettingsWindow.SelectBackupPage"/>, the navigation helper the
/// three command-palette "Backup" entries use to land on the Backup &amp; Restore tab.
///
/// The critical property under test is that Backup is located by its <c>Header</c>, not by a
/// position-based index: a hardcoded "6", or one derived from <c>Items.Count - 1</c>, keeps
/// compiling and passing today but silently points at the wrong tab the moment a tab is
/// reordered/removed ahead of Backup (index shifts down) or a tab is appended after it (Backup is
/// no longer last). <see cref="SelectBackupPage_StillSelectsBackup_IfATabIsAppendedAfterIt"/> is
/// the direction a position-based lookup gets wrong and a hardcoded/derived-index test cannot
/// catch.
/// </summary>
public sealed class SettingsWindowSelectBackupPageTests
{
    [AvaloniaFact]
    public void SelectBackupPage_SelectsTheBackupTab()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow(0);

        var tabs = window.FindControl<TabControl>("MainTabs")!;

        // Start somewhere else, so a no-op SelectBackupPage() implementation can't pass by accident.
        Assert.Equal(0, tabs.SelectedIndex);

        window.SelectBackupPage();

        Assert.Equal("Backup", ((TabItem)tabs.Items[tabs.SelectedIndex]!).Header);
    }

    /// <summary>
    /// Removing an earlier tab shifts Backup's absolute index down without moving it out of the
    /// strip. A header-based lookup is unaffected by this either way, but it is worth pinning
    /// directly: an implementation that instead cached Backup's index once (at construction, say)
    /// would get this wrong.
    /// </summary>
    [AvaloniaFact]
    public void SelectBackupPage_StillSelectsBackup_IfAnEarlierTabIsRemoved()
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

        Assert.Equal("Backup", ((TabItem)tabs.Items[tabs.SelectedIndex]!).Header);
    }

    /// <summary>
    /// The regression an earlier round of this task shipped without catching: a position-based
    /// lookup derived from <c>Items.Count - 1</c> assumes Backup stays the last tab forever. Append
    /// a tab after it - Backup is no longer last - and confirm <c>SelectBackupPage()</c> still
    /// finds it by Header rather than landing on whatever is now last.
    /// </summary>
    [AvaloniaFact]
    public void SelectBackupPage_StillSelectsBackup_IfATabIsAppendedAfterIt()
    {
        using var tree = BackupTestTree.CreateEmpty();
        using var _ = OverrideAppDataRoot(tree.Root);
        var window = new NovaTerminal.SettingsWindow(0);

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        int originalCount = tabs.Items.Count;
        int backupIndexBeforeAppend = originalCount - 1;
        Assert.Equal("Backup", ((TabItem)tabs.Items[backupIndexBeforeAppend]!).Header);

        tabs.Items.Add(new TabItem { Header = "Diagnostics (future tab)" });
        Assert.Equal(originalCount + 1, tabs.Items.Count);

        window.SelectBackupPage();

        // The assertion that matters: SelectedIndex is Backup's own index, not "last item" - which
        // is now the newly-appended tab, a different index entirely.
        Assert.Equal(backupIndexBeforeAppend, tabs.SelectedIndex);
        Assert.NotEqual(tabs.Items.Count - 1, tabs.SelectedIndex);
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
