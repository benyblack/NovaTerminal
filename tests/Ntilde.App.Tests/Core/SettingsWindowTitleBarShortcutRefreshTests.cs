using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Codex round 4, Finding 1 on PR #342 (SettingsWindow.axaml.cs:1337): the Appearance tab's title
/// bar rows show each action's effective shortcut via
/// <c>TitleBarShortcuts.Resolve(entry.ShortcutKey, _shortcutDraftBindings)</c>, computed only when
/// <c>RebuildTitleBarRows</c> runs. Editing a binding on the Shortcuts tab mutates
/// <c>_shortcutDraftBindings</c> without rebuilding those rows, so the Appearance tab kept showing
/// the stale shortcut until Settings was closed and reopened. The fix calls
/// <c>RebuildTitleBarRows()</c> from both mutation sites (the shortcut editor's key handler and the
/// per-row Reset button); this test drives the shared refresh path they both now call and confirms
/// two things at once: the row picks up the new shortcut text, and a title-bar placement change
/// made earlier in the same session is not reset by that refresh (RebuildTitleBarRows only reads
/// _titleBarDraft, a field independent of the shortcut draft, so a rebuild must not lose it).
///
/// <see cref="TestMainWindowFactory"/> already proved a headless Avalonia window with real
/// production wiring can be instantiated in this test host (MainWindow); this is the first test to
/// do the same for SettingsWindow, confirming it is possible here too rather than assuming it isn't.
/// </summary>
public sealed class SettingsWindowTitleBarShortcutRefreshTests
{
    [AvaloniaFact]
    public void RebuildTitleBarRows_AfterAShortcutDraftEdit_ShowsTheUpdatedShortcut_AndKeepsAPriorPlacementChange()
    {
        var window = new NovaTerminal.SettingsWindow();

        // A placement change made before the shortcut edit, exactly like a user pinning something
        // on the Appearance tab first and then editing a binding on the Shortcuts tab in the same
        // session. "find" defaults to Overflow (see TitleBarCatalog), so pinning it is a real
        // change, not a no-op.
        TitleBarDraftState draft = GetTitleBarDraft(window);
        Assert.True(draft.TrySetState("find", TitleBarItemState.Pinned));

        // Simulate what HandleShortcutEditorKeyDown / the Reset button do to the shortcut draft:
        // mutate _shortcutDraftBindings directly, then run the refresh path the fix added.
        Dictionary<string, string> shortcutDraft = GetShortcutDraftBindings(window);
        shortcutDraft["settings"] = "Ctrl+Alt+S";

        InvokeRebuildTitleBarRows(window);

        var panel = window.FindControl<StackPanel>("TitleBarItemsPanel");
        Assert.NotNull(panel);

        var (title, shortcut) = FindRowByTitle(panel!, "Settings");
        Assert.Equal("Settings", title.Text);
        Assert.Equal("Ctrl+Alt+S", shortcut.Text);

        // The earlier placement change must have survived the refresh triggered by the shortcut
        // edit: "find" is still Pinned, and RebuildTitleBarRows rendered it among the pinned rows.
        Assert.Equal(TitleBarItemState.Pinned, draft.GetState("find"));
        var (findTitle, _) = FindRowByTitle(panel!, "Find in Terminal");
        Assert.Equal("Find in Terminal", findTitle.Text);
    }

    private static (TextBlock Title, TextBlock Shortcut) FindRowByTitle(StackPanel panel, string title)
    {
        foreach (var child in panel.Children)
        {
            if (child is not Border { Child: Grid grid })
            {
                continue;
            }

            var labels = grid.Children.OfType<StackPanel>().FirstOrDefault();
            var textBlocks = labels?.Children.OfType<TextBlock>().ToList();
            if (textBlocks is { Count: >= 2 } && textBlocks[0].Text == title)
            {
                return (textBlocks[0], textBlocks[1]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No title bar row found with title '{title}'.");
    }

    private static TitleBarDraftState GetTitleBarDraft(NovaTerminal.SettingsWindow window)
    {
        var field = typeof(NovaTerminal.SettingsWindow).GetField("_titleBarDraft", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TitleBarDraftState)field!.GetValue(window)!;
    }

    private static Dictionary<string, string> GetShortcutDraftBindings(NovaTerminal.SettingsWindow window)
    {
        var field = typeof(NovaTerminal.SettingsWindow).GetField("_shortcutDraftBindings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Dictionary<string, string>)field!.GetValue(window)!;
    }

    private static void InvokeRebuildTitleBarRows(NovaTerminal.SettingsWindow window)
    {
        var method = typeof(NovaTerminal.SettingsWindow).GetMethod("RebuildTitleBarRows", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }
}
