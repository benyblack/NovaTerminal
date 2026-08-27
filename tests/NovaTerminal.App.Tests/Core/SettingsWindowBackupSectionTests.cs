using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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

        Assert.Equal("Restored. Restart NovaTerminal to pick up all changes.", status.Text);

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
