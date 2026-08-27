using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Codex round 6 on PR #342 (finding at <c>MainWindow.axaml.cs:2252</c>): the title bar's
/// right-click "Customize Title Bar..." entry point used to call <c>OpenSettings(0)</c>, which
/// opens the Appearance tab at its default (top) scroll position - the theme editor and preview,
/// well above the TITLE BAR section further down the same tab. Since that section is not
/// independently discoverable (this menu item exists solely to reach it), landing anywhere else
/// defeats the entry point entirely.
///
/// The fix gives <see cref="NovaTerminal.SettingsWindow"/> a third constructor parameter,
/// <see cref="NovaTerminal.SettingsSection"/>, that both (a) forces tab selection to Appearance -
/// the only tab any section currently lives on, regardless of what tab index the caller passed -
/// and (b) schedules a scroll-into-view of the TITLE BAR section header once the window has
/// opened and laid out.
///
/// These tests cover (a) and the fact that the target is recorded, headlessly and without
/// throwing. They do NOT cover the actual scroll offset - see the class remarks on
/// <see cref="Constructing_WithTitleBarSection_DoesNotThrowWhenShownAndLaidOut"/> for why that is
/// out of reach in this test host.
/// </summary>
public sealed class SettingsWindowTitleBarSectionTests
{
    [AvaloniaFact]
    public void Constructing_WithTitleBarSection_SelectsAppearanceTab_EvenWithADifferentInitialTabIndex()
    {
        // initialTab: 2 is Shortcuts - deliberately the "wrong" tab, to prove the section target
        // overrides it rather than merely happening to agree with a caller who already passed 0.
        var window = new NovaTerminal.SettingsWindow(initialTab: 2, section: NovaTerminal.SettingsSection.TitleBar);

        var tabs = window.FindControl<TabControl>("MainTabs");
        Assert.NotNull(tabs);
        Assert.Equal(0, tabs!.SelectedIndex);

        Assert.Equal(NovaTerminal.SettingsSection.TitleBar, GetTargetSection(window));
    }

    [AvaloniaFact]
    public void Constructing_WithoutASection_LeavesExistingTabSelectionBehaviourUnchanged()
    {
        // The two pre-existing callers this must not break: OpenSettings(0) and OpenSettings(1).
        var appearanceWindow = new NovaTerminal.SettingsWindow(0);
        var profilesWindow = new NovaTerminal.SettingsWindow(1);

        Assert.Equal(0, appearanceWindow.FindControl<TabControl>("MainTabs")!.SelectedIndex);
        Assert.Equal(1, profilesWindow.FindControl<TabControl>("MainTabs")!.SelectedIndex);

        Assert.Equal(NovaTerminal.SettingsSection.None, GetTargetSection(appearanceWindow));
        Assert.Equal(NovaTerminal.SettingsSection.None, GetTargetSection(profilesWindow));
    }

    [AvaloniaFact]
    public void ParameterlessConstructor_StillDefaultsToNoSection()
    {
        var window = new NovaTerminal.SettingsWindow();

        Assert.Equal(0, window.FindControl<TabControl>("MainTabs")!.SelectedIndex);
        Assert.Equal(NovaTerminal.SettingsSection.None, GetTargetSection(window));
    }

    /// <summary>
    /// The TITLE BAR section header <see cref="TextBlock"/> that <c>ScrollToTitleBarSection</c>
    /// targets (rather than <c>TitleBarItemsPanel</c>, the rows themselves - see that method's
    /// remarks for why the header is the better target) must actually be reachable by name for the
    /// scroll to do anything at all.
    /// </summary>
    [AvaloniaFact]
    public void TitleBarSectionHeader_IsReachableByName()
    {
        var window = new NovaTerminal.SettingsWindow();

        var header = window.FindControl<TextBlock>("TitleBarSectionHeader");

        Assert.NotNull(header);
        Assert.Equal("TITLE BAR", header!.Text);
    }

    /// <summary>
    /// Drives the real deferred-scroll path end to end - Show() (real layout), draining the
    /// dispatcher queue so the <c>DispatcherPriority.Loaded</c>-posted callback actually runs, and
    /// confirms it does not throw. This does NOT assert the resulting <c>ScrollViewer</c> offset:
    /// no test in this project currently reads a ScrollViewer's real Offset after a headless layout
    /// pass (grepped; the closest precedent, <c>MainWindowTitleBarTests.FindTabHeaderScrollViewer</c>
    /// usage, only ever reads Bounds), and a hand-rolled attempt here found the offset did not
    /// reliably move in the headless test host even after Show() + repeated
    /// <c>Dispatcher.UIThread.RunJobs()</c> - plausibly because the headless platform never assigns
    /// the window a real pixel size, so the ScrollViewer's viewport/extent stay too close to zero
    /// for BringIntoView's math to produce a nonzero offset. Rather than assert a flaky or
    /// always-true offset check, this stops at "the deferred call actually runs and does not
    /// throw," which is the boundary this test host can support without becoming a test that passes
    /// regardless of whether the feature works.
    /// </summary>
    [AvaloniaFact]
    public void Constructing_WithTitleBarSection_DoesNotThrowWhenShownAndLaidOut()
    {
        var window = new NovaTerminal.SettingsWindow(section: NovaTerminal.SettingsSection.TitleBar);

        var exception = Record.Exception(() =>
        {
            window.Show();
            // Opened has fired synchronously by now (Show() raises it); the handler posted the
            // scroll at DispatcherPriority.Loaded, so pump the queue to actually run it.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        });

        Assert.Null(exception);
    }

    private static NovaTerminal.SettingsSection GetTargetSection(NovaTerminal.SettingsWindow window)
    {
        var field = typeof(NovaTerminal.SettingsWindow).GetField("_targetSection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (NovaTerminal.SettingsSection)field!.GetValue(window)!;
    }
}
