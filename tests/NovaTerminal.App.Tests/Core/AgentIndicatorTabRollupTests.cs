using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.AgentHost;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Which attention tiers reach the tab strip (pure policy, mirroring
/// ShellExitPolicyTests), and the wiring that rolls each tab's panes up onto
/// its label via MainWindow.RefreshTabAgentAttention.
///
/// The wiring tests use TestMainWindowFactory.Create(), which runs the real
/// MainWindow constructor. That loads the real on-disk settings.json and then
/// calls AgentHostService.Instance.Apply(settings.AgentAccessObserveEnabled) —
/// reaching the process-wide singleton. On a machine where that setting is
/// persisted as enabled, this would start a real IPC endpoint inside the
/// shared test process (see AgentObserveIndicatorTests, which this mirrors).
/// Every wiring test points NOVATERM_APPDATA_ROOT at a fresh scratch
/// directory for that reason.
///
/// AgentSessionRegistry.Instance is also process-wide, and nothing in this
/// codebase unregisters a pane when a MainWindow used only for a test is
/// abandoned without going through the real tab-close path (Window.Close()
/// alone does not call DisposeControlTree). So an assertion of
/// Assert.Single(AgentSessionRegistry.Instance.GetRegistrations()) would be
/// hostage to whatever other tests in the same process happened to run
/// first. Instead, each wiring test snapshots the registry immediately
/// before creating its window and diffs against that snapshot afterwards, so
/// the assertion is about what *this* window added, not the global count.
/// </summary>
public sealed class AgentIndicatorTabRollupTests
{
    [Theory]
    // WritesOnly (the default): only a write reaches the tab strip.
    [InlineData("WritesOnly", AgentAttentionTier.Idle, false)]
    [InlineData("WritesOnly", AgentAttentionTier.Watched, false)]
    [InlineData("WritesOnly", AgentAttentionTier.Wrote, true)]
    // All: reads surface too.
    [InlineData("All", AgentAttentionTier.Idle, false)]
    [InlineData("All", AgentAttentionTier.Watched, true)]
    [InlineData("All", AgentAttentionTier.Wrote, true)]
    // Unrecognised and absent values fall back to the quieter behaviour: a
    // typo must not make the chrome noisier than the default.
    [InlineData("all", AgentAttentionTier.Watched, false)]
    [InlineData("Everything", AgentAttentionTier.Watched, false)]
    [InlineData("", AgentAttentionTier.Watched, false)]
    [InlineData(null, AgentAttentionTier.Watched, false)]
    // ...but a write still shows under any policy value.
    [InlineData("Everything", AgentAttentionTier.Wrote, true)]
    [InlineData(null, AgentAttentionTier.Wrote, true)]
    public void Rollup_policy_selects_which_tiers_reach_the_tab_strip(
        string? policy, AgentAttentionTier tier, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldShowTierInTabStrip(policy, tier));
    }

    [Fact]
    public void Default_setting_value_is_writes_only()
    {
        Assert.Equal("WritesOnly", new TerminalSettings().AgentIndicatorTabRollup);
    }

    [AvaloniaFact]
    public void A_write_marks_the_owning_tabs_label()
    {
        RunIsolated((window, registration) =>
        {
            registration.AttentionMachine.NoteWrote("sendInput");
            window.RefreshTabAgentAttention();

            Assert.Contains(MainWindow.AgentWroteGlyph, FirstTabLabel(window));
        });
    }

    [AvaloniaFact]
    public void A_read_does_not_mark_the_tab_under_the_default_policy()
    {
        RunIsolated((window, registration) =>
        {
            registration.AttentionMachine.NoteRead();
            window.RefreshTabAgentAttention();

            var label = FirstTabLabel(window);
            Assert.DoesNotContain(MainWindow.AgentWatchedGlyph, label);
            Assert.DoesNotContain(MainWindow.AgentWroteGlyph, label);
        });
    }

    [AvaloniaFact]
    public void A_read_marks_the_tab_under_the_All_policy()
    {
        RunIsolated((window, registration) =>
        {
            SetRollupPolicy(window, "All");

            registration.AttentionMachine.NoteRead();
            window.RefreshTabAgentAttention();

            Assert.Contains(MainWindow.AgentWatchedGlyph, FirstTabLabel(window));
        });
    }

    [AvaloniaFact]
    public void Updating_tab_visuals_preserves_the_marker()
    {
        // UpdateTabVisuals rewrites every label from scratch, so the marker has
        // to come from tab state, not be patched onto the label after the fact.
        RunIsolated((window, registration) =>
        {
            registration.AttentionMachine.NoteWrote("sendInput");
            window.RefreshTabAgentAttention();

            window.UpdateTabVisuals();

            Assert.Contains(MainWindow.AgentWroteGlyph, FirstTabLabel(window));
        });
    }

    // Step 4b finding: BuildTabDisplayLabels truncates the full label (which
    // already carries the agent suffix, appended in BuildFullTabLabel) to 44
    // characters with no room reserved for it — the same exposure the
    // pre-existing bell/activity suffixes have. A long enough tab title drops
    // the marker from the visible header. This is a documented failing
    // expectation, not a bug fixed by this task: see task-6-report.md for the
    // measured label string and the reasoning for leaving the truncation path
    // alone.
    [AvaloniaFact(Skip = "Step 4b finding: label truncation (BuildTabDisplayLabels -> TruncateTabLabel) drops the agent suffix on a long tab title. Documented as a failing expectation per task-6-brief.md Step 4b; see task-6-report.md.")]
    public void A_long_tab_title_does_not_lose_the_marker_to_truncation()
    {
        RunIsolated((window, registration) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tab = tabs.Items.Cast<TabItem>().First();
            SetTabUserTitle(window, tab, new string('x', 60));

            registration.AttentionMachine.NoteWrote("sendInput");
            window.RefreshTabAgentAttention();

            Assert.Contains(MainWindow.AgentWroteGlyph, FirstTabLabel(window));
        });
    }

    private static void RunIsolated(Action<MainWindow, AgentSessionRegistration> body)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"novaterm_tab_rollup_test_{Guid.NewGuid():N}");
        string? previousRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);

            var before = AgentSessionRegistry.Instance.GetRegistrations();
            var window = TestMainWindowFactory.Create();
            window.Show();

            var added = AgentSessionRegistry.Instance.GetRegistrations().Except(before).ToArray();
            var registration = Assert.Single(added);

            body(window, registration);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string FirstTabLabel(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        var host = (Border)tab.Header!;
        return ((TextBlock)host.Child!).Text ?? string.Empty;
    }

    private static void SetRollupPolicy(MainWindow window, string policy)
    {
        var settings = (TerminalSettings)typeof(MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        settings.AgentIndicatorTabRollup = policy;
    }

    private static void SetTabUserTitle(MainWindow window, TabItem tab, string title)
    {
        var getOrCreateTabState = typeof(MainWindow)
            .GetMethod("GetOrCreateTabState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = getOrCreateTabState.Invoke(window, new object[] { tab })!;
        state.GetType().GetProperty("UserTitle")!.SetValue(state, title);
    }
}
