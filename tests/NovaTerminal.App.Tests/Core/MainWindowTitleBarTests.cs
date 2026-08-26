using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NovaTerminal.Shell;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers MainWindow.RebuildTitleBar - the piece the plan's full-suite verification found had zero
/// automated coverage, on the (wrong) assumption that MainWindow cannot be instantiated headless.
/// <see cref="TestMainWindowFactory"/> proves it can, so these tests drive the real rebuild path
/// through reflection, the same way MainWindowStartupTests already does.
///
/// Lookups here walk "TitleBarItemsHost".Children by name rather than calling
/// <c>window.FindControl&lt;Button&gt;(TitleBarViewFactory.ButtonName(id))</c>. That call resolves
/// only through the window's compiled NameScope (verified directly against
/// NameScope.GetNameScope(window)?.Find&lt;T&gt;, which agreed with FindControl's true/false in
/// every case tried, with or without Show()/a layout pass) and title bar buttons are created at
/// runtime by TitleBarViewFactory, so they are never registered into it - FindControl returns null
/// for them both here and in the shipped app. See task-10-report.md for the production-code impact.
/// </summary>
public sealed class MainWindowTitleBarTests
{
    // TabOverflowBadge is inserted immediately after the Tab List button by
    // MainWindow.PlaceTabOverflowBadge - see
    // RebuildTitleBar_DefaultSettings_BadgeSitsImmediatelyAfterTabListButton below for the dedicated
    // adjacency test. This list has to include it too since it asserts the host's full children order.
    private static readonly string[] DefaultPinnedButtonNames =
    [
        "BtnNewTab",
        TitleBarViewFactory.ButtonName("open_tab_list"),
        "TabOverflowBadge",
        TitleBarViewFactory.ButtonName("connections"),
        TitleBarViewFactory.ButtonName("settings"),
        TitleBarViewFactory.OverflowButtonName,
    ];

    [AvaloniaFact]
    public void DefaultSettings_ProduceExpectedPinnedButtonsInOrder_PlusOverflow()
    {
        var window = TestMainWindowFactory.Create();

        var host = GetTitleBarHost(window);
        var actualNames = host.Children.Select(c => (c as Control)?.Name).ToList();

        Assert.Equal(DefaultPinnedButtonNames, actualNames);
    }

    [AvaloniaFact]
    public void RebuildTitleBar_APinnedExtraAction_Appears()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarItems["find"] = "Pinned";
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);

        Assert.Contains(
            TitleBarViewFactory.ButtonName("find"),
            host.Children.Select(c => (c as Control)?.Name));
    }

    [AvaloniaFact]
    public void RebuildTitleBar_AHiddenAction_DisappearsFromTheBarAndTheOverflowFlyout()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarItems["connections"] = "Hidden";
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);

        Assert.DoesNotContain(
            TitleBarViewFactory.ButtonName("connections"),
            host.Children.Select(c => (c as Control)?.Name));

        var overflowButton = host.Children.OfType<Button>()
            .SingleOrDefault(b => b.Name == TitleBarViewFactory.OverflowButtonName);
        Assert.NotNull(overflowButton);
        var flyout = Assert.IsType<MenuFlyout>(overflowButton!.Flyout);

        Assert.DoesNotContain(
            flyout.Items.OfType<MenuItem>(),
            item => ((string?)item.Header)?.StartsWith("Connections", System.StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The + button is declared in XAML and carries a real MenuFlyout; TitleBarViewFactory
    /// reinserts the same instance on every rebuild instead of rebuilding it, specifically so that
    /// flyout survives. This is the hazard that design exists to prevent.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TheNewTabButton_SurvivesRebuildsAsTheSameInstance()
    {
        var window = TestMainWindowFactory.Create();
        var original = window.FindControl<Button>("BtnNewTab");
        Assert.NotNull(original);

        InvokeRebuildTitleBar(window);
        InvokeRebuildTitleBar(window);

        var afterRebuilds = window.FindControl<Button>("BtnNewTab");
        Assert.Same(original, afterRebuilds);
    }

    /// <summary>
    /// TabOverflowBadge is not one of the catalog buttons TitleBarViewFactory.Populate builds, so its
    /// unconditional host.Children.Clear() would destroy the badge outright if it were a permanent
    /// child of TitleBarItemsHost. MainWindow.PlaceTabOverflowBadge re-parents the same TextBlock
    /// instance back into (or out of) the host after every Populate() call instead of ever letting a
    /// new one get created - this asserts the instance really does survive a rebuild rather than
    /// getting silently replaced.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TabOverflowBadge_SurvivesRebuild()
    {
        var window = TestMainWindowFactory.Create();
        var before = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(before);

        InvokeRebuildTitleBar(window);

        var after = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(after);
        Assert.Same(before, after);
    }

    /// <summary>
    /// With Tab List Pinned, the badge sits immediately after its button in TitleBarItemsHost -
    /// asserted here as an index relationship because RebuildTitleBar rebuilds both around it and the
    /// concrete index shifts depending on what else is pinned.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_DefaultSettings_BadgeSitsImmediatelyAfterTabListButton()
    {
        var window = TestMainWindowFactory.Create();

        var host = GetTitleBarHost(window);
        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);

        int tabListIndex = host.Children.IndexOf(
            host.Children.OfType<Button>().Single(b => b.Name == TitleBarViewFactory.ButtonName("open_tab_list")));
        int badgeIndex = host.Children.IndexOf(badge);

        Assert.Equal(tabListIndex + 1, badgeIndex);
    }

    /// <summary>
    /// Codex P2 round 2 on PR #342: a fixed Grid.Column="0" placement kept the badge structurally
    /// separate from the Tab List button, so reordering the pinned set could put several buttons
    /// between them. Moving Tab List later in TitleBarOrder must not break the adjacency.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TabListReorderedLater_BadgeStillFollowsIt()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarOrder.Clear();
        settings.TitleBarOrder.AddRange(["connections", "settings", "open_tab_list"]);
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);
        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);

        int tabListIndex = host.Children.IndexOf(
            host.Children.OfType<Button>().Single(b => b.Name == TitleBarViewFactory.ButtonName("open_tab_list")));
        int badgeIndex = host.Children.IndexOf(badge);

        Assert.True(tabListIndex > 1, "Tab List should have moved later than its default (index 1) position for this test to mean anything.");
        Assert.Equal(tabListIndex + 1, badgeIndex);
    }

    /// <summary>
    /// With Tab List Hidden there is no button for the badge to sit beside, so it must not be in the
    /// host at all (which would otherwise leave a trailing, un-anchored badge) and must not be
    /// visible.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TabListHidden_BadgeIsNotInHostAndNotVisible()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarItems["open_tab_list"] = "Hidden";
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);
        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);

        Assert.DoesNotContain(host.Children, c => ReferenceEquals(c, badge));
        Assert.False(badge!.IsVisible);
    }

    /// <summary>
    /// Avalonia throws when a control that still has a logical parent is added to a different one.
    /// PlaceTabOverflowBadge detaches the badge from wherever it currently lives before re-inserting
    /// it, but Populate()'s host.Children.Clear() also detaches it if it was previously inside the
    /// host - two consecutive rebuilds exercise both of those detach paths back to back and must not
    /// throw, ending with the badge correctly placed either time.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TwoConsecutiveRebuilds_LeaveBadgeCorrectlyPlacedWithoutThrowing()
    {
        var window = TestMainWindowFactory.Create();

        InvokeRebuildTitleBar(window);
        var exception = Record.Exception(() => InvokeRebuildTitleBar(window));
        Assert.Null(exception);

        var host = GetTitleBarHost(window);
        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);

        int tabListIndex = host.Children.IndexOf(
            host.Children.OfType<Button>().Single(b => b.Name == TitleBarViewFactory.ButtonName("open_tab_list")));
        int badgeIndex = host.Children.IndexOf(badge);

        Assert.Equal(tabListIndex + 1, badgeIndex);
    }

    /// <summary>
    /// Codex P2 on PR #342: the badge lives outside TitleBarItemsHost while Tab List is not Pinned
    /// specifically so RebuildTitleBar (which clears that host's children on every call) does not
    /// orphan it - but that same independence meant UpdateTabOverflowIndicator's old null-button
    /// early return left a stale "+N" visible with no adjacent Tab List button once open_tab_list
    /// moved from Pinned to Overflow/Hidden while tabs were clipped. This forces the badge into a
    /// visible "clipped" state, flips open_tab_list out of Pinned, and asserts the next indicator
    /// update hides and clears it instead of leaving it stuck.
    /// </summary>
    [AvaloniaFact]
    public void UpdateTabOverflowIndicator_TabListNoLongerPinned_ClearsAStaleBadge()
    {
        var window = TestMainWindowFactory.Create();
        // FindTabHeaderScrollViewer walks GetVisualDescendants() for the TabControl's templated
        // PART_TabHeaderScrollViewer, which only materializes once the control template is applied -
        // Show() is what does that under headless Avalonia (same pattern as
        // TerminalPaneSshDisconnectTests and the CommandAssist layout tests). Without it,
        // UpdateTabOverflowIndicator's scrollViewer==null guard would return before ever reaching the
        // code this test targets, passing for the wrong reason.
        window.Show();
        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);

        // Simulate the badge already showing a clipped-tabs indicator.
        badge!.IsVisible = true;
        badge.Text = "+3";

        var settings = GetSettings(window);
        settings.TitleBarItems["open_tab_list"] = "Overflow";
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);
        Assert.DoesNotContain(
            TitleBarViewFactory.ButtonName("open_tab_list"),
            host.Children.Select(c => (c as Control)?.Name));

        InvokeUpdateTabOverflowIndicator(window);

        Assert.False(badge.IsVisible);
        Assert.Equal(string.Empty, badge.Text);
    }

    [AvaloniaFact]
    public void RebuildTitleBar_Record_AutoSurfacesWhileActive_AndReturnsToOverflowWhenNotActive()
    {
        var window = TestMainWindowFactory.Create();
        var activeToggles = GetActiveTitleBarToggles(window);
        string recordButtonName = TitleBarViewFactory.ButtonName("toggle_recording");

        // toggle_recording defaults to Overflow, so at rest it is not in the bar.
        var hostAtRest = GetTitleBarHost(window);
        Assert.DoesNotContain(recordButtonName, hostAtRest.Children.Select(c => (c as Control)?.Name));

        activeToggles.Add("toggle_recording");
        InvokeRebuildTitleBar(window);

        var hostWhileActive = GetTitleBarHost(window);
        Assert.Contains(recordButtonName, hostWhileActive.Children.Select(c => (c as Control)?.Name));

        activeToggles.Remove("toggle_recording");
        InvokeRebuildTitleBar(window);

        var hostAfterDeactivation = GetTitleBarHost(window);
        Assert.DoesNotContain(recordButtonName, hostAfterDeactivation.Children.Select(c => (c as Control)?.Name));
    }

    /// <summary>
    /// Task 11 regression coverage: Tasks 5-7 wired every call site in MainWindow.axaml.cs that
    /// touches a generated title bar button through
    /// <c>this.FindControl&lt;Button&gt;(TitleBarViewFactory.ButtonName(id))</c>, which - per the
    /// class remark above - can never resolve a runtime-created child and silently returns null.
    /// The null-checks at each call site turned that into a quiet no-op instead of a crash, which
    /// is exactly why three tasks of review missed it. These tests drive the real private methods
    /// (via reflection, same as the rest of this file) and assert on a property each one actually
    /// mutates that is NOT inherited from the window, since an inherited property (e.g.
    /// Foreground, when never locally set) reads correctly from the window even when the lookup
    /// underneath is completely dead - that inheritance fallback is what masked the defect.
    /// </summary>
    [AvaloniaFact]
    public void UpdateRecordButtonUi_Recolors_GeneratedRecordButtonBackground()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);
        settings.TitleBarItems["toggle_recording"] = "Pinned";
        InvokeRebuildTitleBar(window);

        var button = GetGeneratedButton(window, "toggle_recording");
        var initialBackground = button.Background;

        InvokeUpdateRecordButtonUi(window, isRecording: true);

        // Background is a locally-set property on every generated button (Brushes.Transparent at
        // creation - see TitleBarViewFactory.CreateItemButton), so it never falls back to an
        // inherited value: if FindTitleBarButton's lookup were dead, this assertion would fail
        // rather than pass on a false positive, unlike Foreground would. Asserted through
        // ISolidColorBrush rather than a concrete IsType check, since Brushes.Transparent (the
        // untouched initial value) is an ImmutableSolidColorBrush while the active-state brush the
        // production code assigns is a mutable SolidColorBrush - both implement ISolidColorBrush,
        // and pinning to one concrete type would make the assertion fail for that incidental
        // reason instead of the one this test actually cares about.
        var activeBrush = Assert.IsAssignableFrom<ISolidColorBrush>(button.Background);
        Assert.Equal(Color.Parse("#30F1636B"), activeBrush.Color);
        Assert.NotEqual(initialBackground, button.Background);
    }

    [AvaloniaFact]
    public void PopulateTabListMenu_ReachesGeneratedButton_AndAttachesMenuFlyout()
    {
        var window = TestMainWindowFactory.Create();

        // open_tab_list is pinned by default (see DefaultPinnedButtonNames), and unlike the
        // overflow button, TitleBarViewFactory does not give a pinned item button a MenuFlyout up
        // front - PopulateTabListMenu is supposed to get-or-create one on first use.
        var button = GetGeneratedButton(window, "open_tab_list");
        Assert.Null(button.Flyout);

        InvokePopulateTabListMenu(window, showFlyout: false);

        Assert.IsType<MenuFlyout>(button.Flyout);
    }

    /// <summary>
    /// Codex P2 on PR #342: PopulateTabListMenu used to early-return whenever
    /// <c>FindTitleBarButton(TitleBarCatalog.OpenTabListId)</c> came back null, which is exactly
    /// what happens once the user sets Tab List to Hidden - the dedicated button is gone by design.
    /// That made the overflow menu entry, the Ctrl+Shift+O shortcut, and the command palette entry
    /// all silent no-ops, defeating the entire premise of Hidden ("still reachable, just not a
    /// dedicated icon"). This drives the real private method with Tab List set to Hidden and proves
    /// it still resolves an anchor and builds a menu instead of bailing out: the production code
    /// only populates <c>_tabListFallbackFlyout</c> once it gets past the old null-button return.
    /// </summary>
    [AvaloniaFact]
    public void PopulateTabListMenu_TabListHidden_StillResolvesAnAnchorAndBuildsTheMenu()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarItems["open_tab_list"] = "Hidden";
        InvokeRebuildTitleBar(window);

        // The dedicated button is gone, exactly as Hidden intends.
        var host = GetTitleBarHost(window);
        Assert.DoesNotContain(
            TitleBarViewFactory.ButtonName("open_tab_list"),
            host.Children.Select(c => (c as Control)?.Name));

        // Reset the field to null to guarantee a clean baseline, making the assertion unambiguous:
        // any non-null result can only have come from the call under test, not from prior state.
        SetTabListFallbackFlyout(window, null);

        InvokePopulateTabListMenu(window, showFlyout: true);

        // A non-null fallback flyout is only ever set on the path past the old bailout - the bug
        // this guards against left it null forever because the method returned before reaching it.
        var flyout = GetTabListFallbackFlyout(window);
        Assert.NotNull(flyout);
        Assert.True(flyout!.Items.Count > 0, "The fallback flyout should have been populated with menu items");
    }

    /// <summary>
    /// ApplyThemeToUI only ever assigns Foreground on the generated title bar buttons (see
    /// MainWindow.axaml.cs around line 4404) - every property it touches on them is inherited from
    /// the window, which already carries the same contrastForeground brush by the time these lines
    /// run. That means asserting Foreground alone would pass even with a totally dead
    /// FindTitleBarButton lookup, the same masking documented on the other two tests in this
    /// group. To make the assertion meaningful, this test first gives the button a local
    /// (non-inherited) sentinel Foreground value; only a real assignment inside ApplyThemeToUI -
    /// meaning the lookup actually found the live button - can overwrite it back to the shared
    /// contrastForeground instance also applied to the window itself.
    /// </summary>
    [AvaloniaFact]
    public void ApplyThemeToUI_Recolors_GeneratedConnectionsButtonForeground()
    {
        var window = TestMainWindowFactory.Create();
        var button = GetGeneratedButton(window, "connections");
        button.Foreground = Brushes.Lime;

        InvokeApplyThemeToUI(window);

        Assert.NotEqual(Brushes.Lime, button.Foreground);
        Assert.Same(window.Foreground, button.Foreground);
    }

    private static Button GetGeneratedButton(NovaTerminal.MainWindow window, string catalogId)
    {
        var host = GetTitleBarHost(window);
        string name = TitleBarViewFactory.ButtonName(catalogId);
        var button = host.Children.OfType<Button>().SingleOrDefault(b => b.Name == name);
        Assert.NotNull(button);
        return button!;
    }

    private static void InvokeUpdateRecordButtonUi(NovaTerminal.MainWindow window, bool isRecording)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("UpdateRecordButtonUi", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, [isRecording]);
    }

    private static void InvokePopulateTabListMenu(NovaTerminal.MainWindow window, bool showFlyout)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("PopulateTabListMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, [showFlyout]);
    }

    private static void InvokeUpdateTabOverflowIndicator(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("UpdateTabOverflowIndicator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }

    private static MenuFlyout? GetTabListFallbackFlyout(NovaTerminal.MainWindow window)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_tabListFallbackFlyout", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (MenuFlyout?)field!.GetValue(window);
    }

    private static void SetTabListFallbackFlyout(NovaTerminal.MainWindow window, MenuFlyout? value)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_tabListFallbackFlyout", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }

    private static void InvokeApplyThemeToUI(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("ApplyThemeToUI", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }

    private static StackPanel GetTitleBarHost(NovaTerminal.MainWindow window)
    {
        var host = window.FindControl<StackPanel>("TitleBarItemsHost");
        Assert.NotNull(host);
        return host!;
    }

    private static TerminalSettings GetSettings(NovaTerminal.MainWindow window)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TerminalSettings)field!.GetValue(window)!;
    }

    private static HashSet<string> GetActiveTitleBarToggles(NovaTerminal.MainWindow window)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_activeTitleBarToggles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(window)!;
    }

    private static void InvokeRebuildTitleBar(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }
}
