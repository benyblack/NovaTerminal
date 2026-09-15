using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
/// These tests cover (a) and, per
/// <see cref="Constructing_WithTitleBarSection_ScrollsHeaderIntoView_WhenScrolledAwayBeforehand"/>,
/// (b) as well: round 6 originally reported the actual scroll offset as out of reach in this test
/// host, attributing a stuck-at-zero <c>ScrollViewer.Offset</c> to the headless window never
/// getting real pixel dimensions. That reasoning does not survive inspection - instrumenting the
/// same host showed <c>Window.Bounds</c>, <c>ScrollViewer.Extent</c>, and
/// <c>ScrollViewer.Viewport</c> all resolve to real, non-zero pixel sizes immediately after
/// <c>Show()</c>, even before any <c>RunJobs()</c> call (consistent with round 7's
/// <c>TitleBar.Bounds.Width</c> resolving in the same host). The offset stayed at zero for an
/// unrelated reason: at <see cref="NovaTerminal.SettingsWindow"/>'s default 880x620 size, the
/// TITLE BAR header already sits inside the initial (zero-offset) viewport, so
/// <c>BringIntoView</c> was a legitimate no-op, not a broken one. See that test for how forcing the
/// header off-screen first makes the assertion meaningful.
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
    /// confirms it does not throw. Kept as a narrow smoke test of the event-wiring itself (the
    /// <c>Opened += ... Dispatcher.Post(..., DispatcherPriority.Loaded)</c> plumbing); the actual
    /// scroll *effect* is covered separately by
    /// <see cref="Constructing_WithTitleBarSection_ScrollsHeaderIntoView_WhenScrolledAwayBeforehand"/>,
    /// which is the test that can actually fail if the scroll stops working.
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

    /// <summary>
    /// Asserts the actual effect of <c>ScrollToTitleBarSection</c>: that it leaves the TITLE BAR
    /// header within the <see cref="ScrollViewer"/>'s viewport.
    /// </summary>
    /// <remarks>
    /// Round 6 could not get <c>ScrollViewer.Offset</c> to move off zero and attributed that to the
    /// headless window never getting real pixel dimensions. That is false - instrumenting this same
    /// host shows <c>Window.Bounds</c>, <c>ScrollViewer.Extent</c> (672x2253) and
    /// <c>ScrollViewer.Viewport</c> (672x567) are all real and non-zero immediately after
    /// <c>Show()</c>, before any <c>RunJobs()</c>. The true reason the offset stayed at zero: at
    /// <see cref="NovaTerminal.SettingsWindow"/>'s default 880x620 size, the TITLE BAR header (at
    /// content-relative Y=348) already sits inside the zero-offset viewport (0..567) - so
    /// <c>BringIntoView</c> had nothing to do. A test that opens the window at its default size and
    /// merely checks the offset moved, or checks the header is visible, would pass identically
    /// whether or not the scroll call exists (see the RED verification note below) - exactly the
    /// "passes regardless of whether the feature works" trap round 6 was rightly avoiding.
    /// <para>
    /// To get a test that can actually fail, this scrolls the <see cref="ScrollViewer"/> away from
    /// the header first (to its maximum offset, past the header entirely, and lets that commit
    /// through a real layout pass) before invoking the same private
    /// <c>ScrollToTitleBarSection</c> method the <c>Opened</c> handler posts (reached via
    /// reflection, the same technique <see cref="GetTargetSection"/> already uses in this file).
    /// That reproduces the "opened with the target off-screen" condition the feature exists for,
    /// regardless of how tall this test host happens to render the content above the header.
    /// </para>
    /// <para>
    /// The header's own position within the scrolled content is captured once, at the window's
    /// natural (zero, unscrolled) offset - the only point where <c>TextBlock.Bounds.Top</c> is
    /// unambiguously an absolute content-space coordinate (once scrolled, the intermediate
    /// <c>ScrollContentPresenter</c>/panel Bounds used to reach the same figure become
    /// arrange-timing-sensitive in this host, so re-deriving it after scrolling is not reliable -
    /// this sidesteps that entirely by only ever reading Bounds at offset zero). The final assertion
    /// allows a 40px tolerance either side of the header's true position: BringIntoView is observed
    /// to consistently land ~14-28px past the header's top edge in this host (a real, reproducible,
    /// but small alignment margin - most plausibly from a manual page-scroll-style function it uses
    /// - not a failure to scroll), and that is not what this test is guarding against. What it does
    /// catch: a scroll call that does nothing (offset stays at the forced-away ~1686) or one that
    /// lands nowhere near the header, which is what "landing on the wrong section" looks like.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void Constructing_WithTitleBarSection_ScrollsHeaderIntoView_WhenScrolledAwayBeforehand()
    {
        var window = new NovaTerminal.SettingsWindow(section: NovaTerminal.SettingsSection.TitleBar);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // real layout; drains the natural (here, no-op) auto-scroll too

        var header = window.FindControl<TextBlock>("TitleBarSectionHeader");
        Assert.NotNull(header);

        var sv = FindAncestorScrollViewer(header!);
        Assert.NotNull(sv);

        // Ground truth: the header's absolute content-space position, read at the one offset (zero)
        // where Bounds is unambiguous. Scrolling doesn't reflow the content, so this stays valid.
        double headerTop = header!.Bounds.Top;
        double headerBottom = header.Bounds.Bottom;

        // Force the header off-screen and let it commit through a real, settled layout pass -
        // reproducing "opened with the target scrolled out of view," which is what
        // ScrollToTitleBarSection exists to fix.
        sv!.Offset = new Avalonia.Vector(0, sv.Extent.Height);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(sv.Offset.Y > headerBottom + 100, $"Test setup failed to actually scroll the header out of view (offset={sv.Offset}, header=[{headerTop},{headerBottom}]).");

        var method = typeof(NovaTerminal.SettingsWindow).GetMethod("ScrollToTitleBarSection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        const double tolerance = 40;
        Assert.InRange(sv.Offset.Y, headerTop - tolerance, headerBottom + tolerance);
    }

    private static ScrollViewer? FindAncestorScrollViewer(Avalonia.Visual visual)
    {
        Avalonia.Visual? v = visual;
        while (v != null)
        {
            if (v is ScrollViewer found) return found;
            v = v.GetVisualParent();
        }
        return null;
    }

    private static NovaTerminal.SettingsSection GetTargetSection(NovaTerminal.SettingsWindow window)
    {
        var field = typeof(NovaTerminal.SettingsWindow).GetField("_targetSection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (NovaTerminal.SettingsSection)field!.GetValue(window)!;
    }
}
