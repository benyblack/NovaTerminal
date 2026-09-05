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
public sealed class AgentIndicatorTabRollupTests : IDisposable
{
    /// <summary>
    /// Disposes the panes of every window this class asked for, and with them the real shells
    /// behind them. xUnit builds a fresh instance per test, so this runs after each one.
    /// </summary>
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

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
    public void Tightening_the_rollup_policy_clears_a_displayed_read_glyph()
    {
        // The policy filter is baked into the tab's stored tier by
        // RefreshTabAgentAttention, so nothing re-evaluates it on its own. The
        // settings-applied path used to refresh only the window-level indicator,
        // leaving a displayed read glyph on the tab until the next attention
        // event on that pane — which, for a pane an agent has stopped touching,
        // may never come. ApplyAgentHostSettingsLive is the exact method the
        // Settings dialog's save path calls.
        RunIsolated((window, registration) =>
        {
            SetRollupPolicy(window, "All");
            registration.AttentionMachine.NoteRead();
            window.RefreshTabAgentAttention();
            Assert.Contains(MainWindow.AgentWatchedGlyph, FirstTabLabel(window));

            SetRollupPolicy(window, "WritesOnly");
            window.ApplyAgentHostSettingsLive();

            Assert.DoesNotContain(MainWindow.AgentWatchedGlyph, FirstTabLabel(window));
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

    // Step 4b originally found this marker dropped by truncation (see
    // task-6-report.md's "Step 4b" section for the measured evidence: a
    // 60-char title truncated to "xxx...x…" with the glyph gone). The fix
    // round restructured BuildTabDisplayLabels to reserve room for the
    // marker ahead of truncation, so this now passes for real. No longer
    // skipped.
    [AvaloniaFact]
    public void A_long_tab_title_does_not_lose_the_write_marker_to_truncation()
    {
        RunIsolated((window, registration) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tab = tabs.Items.Cast<TabItem>().First();
            SetTabUserTitle(window, tab, new string('x', 60));

            registration.AttentionMachine.NoteWrote("sendInput");
            window.RefreshTabAgentAttention();

            string label = FirstTabLabel(window);
            Assert.Contains(MainWindow.AgentWroteGlyph, label);
            Assert.True(label.Length <= 44, $"expected label within the 44-char budget, got {label.Length}: \"{label}\"");
        });
    }

    [AvaloniaFact]
    public void A_long_tab_title_does_not_lose_the_bell_marker_to_truncation()
    {
        RunIsolated((window, _) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tab = tabs.Items.Cast<TabItem>().First();
            SetTabUserTitle(window, tab, new string('x', 60));
            SetTabBell(window, tab, true);

            window.UpdateTabVisuals();

            string label = FirstTabLabel(window);
            Assert.Contains("🔔", label);
            Assert.True(label.Length <= 44, $"expected label within the 44-char budget, got {label.Length}: \"{label}\"");
        });
    }

    [AvaloniaFact]
    public void A_long_tab_title_does_not_lose_the_activity_marker_to_truncation()
    {
        RunIsolated((window, _) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tab = tabs.Items.Cast<TabItem>().First();
            SetTabUserTitle(window, tab, new string('x', 60));
            SetTabActivity(window, tab, true);

            window.UpdateTabVisuals();

            string label = FirstTabLabel(window);
            Assert.Contains("•", label);
            Assert.True(label.Length <= 44, $"expected label within the 44-char budget, got {label.Length}: \"{label}\"");
        });
    }

    [AvaloniaFact]
    public void A_long_tab_title_with_a_bell_and_a_write_retains_both_markers_in_order()
    {
        // Bell/activity are mutually exclusive, but the agent tier is
        // independent of both and can accompany either — this pins that
        // combination surviving truncation together, in the same order
        // (bell, then agent) that BuildFullTabLabel has always produced.
        RunIsolated((window, registration) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tab = tabs.Items.Cast<TabItem>().First();
            SetTabUserTitle(window, tab, new string('x', 60));
            SetTabBell(window, tab, true);

            registration.AttentionMachine.NoteWrote("sendInput");
            window.RefreshTabAgentAttention();

            string label = FirstTabLabel(window);
            int bellIndex = label.IndexOf("🔔", StringComparison.Ordinal);
            int agentIndex = label.IndexOf(MainWindow.AgentWroteGlyph, StringComparison.Ordinal);
            Assert.True(bellIndex >= 0, $"expected a bell marker in \"{label}\"");
            Assert.True(agentIndex >= 0, $"expected a write marker in \"{label}\"");
            Assert.True(bellIndex < agentIndex, $"expected the bell marker before the write marker in \"{label}\"");
            Assert.True(label.Length <= 44, $"expected label within the 44-char budget, got {label.Length}: \"{label}\"");
        });
    }

    [AvaloniaFact]
    public void Colliding_long_titles_with_a_marker_keep_both_the_hint_and_the_marker()
    {
        // Both tabs share the same long title and the same marker (a bell),
        // so their truncated base+marker strings collide and the
        // disambiguation hint kicks in for both. The marker must stay
        // adjacent to the title (as it is when nothing collides), with the
        // hint appended after it — never the other way around.
        RunIsolated((window, _) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var firstTab = tabs.Items.Cast<TabItem>().First();
            string longTitle = new string('x', 60);
            SetTabUserTitle(window, firstTab, longTitle);
            SetTabBell(window, firstTab, true);

            var secondTab = AddBareTab(window, longTitle);
            SetTabUserTitle(window, secondTab, longTitle);
            SetTabBell(window, secondTab, true);

            window.UpdateTabVisuals();

            var labels = tabs.Items.Cast<TabItem>()
                .Select(t => ((TextBlock)((Border)t.Header!).Child!).Text ?? string.Empty)
                .ToArray();
            Assert.Equal(2, labels.Length);
            Assert.NotEqual(labels[0], labels[1]);

            foreach (var label in labels)
            {
                int bellIndex = label.IndexOf("🔔", StringComparison.Ordinal);
                int hintIndex = label.IndexOf('~');
                Assert.True(bellIndex >= 0, $"expected a bell marker in \"{label}\"");
                Assert.True(hintIndex >= 0, $"expected a disambiguation hint in \"{label}\"");
                Assert.True(bellIndex < hintIndex, $"expected the marker before the hint in \"{label}\"");
                Assert.True(label.Length <= 44, $"expected label within the 44-char budget, got {label.Length}: \"{label}\"");
            }
        });
    }

    [AvaloniaFact]
    public void A_short_tab_title_with_a_marker_is_unaffected_by_the_refactor()
    {
        // No truncation is ever warranted here, so the marker-reserving
        // budget math must be a no-op: same output as before this fix round.
        RunIsolated((window, registration) =>
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var tab = tabs.Items.Cast<TabItem>().First();
            SetTabUserTitle(window, tab, "Short");

            registration.AttentionMachine.NoteWrote("sendInput");
            window.RefreshTabAgentAttention();

            string label = FirstTabLabel(window);
            Assert.Contains(MainWindow.AgentWroteGlyph, label);
            Assert.DoesNotContain("…", label);
            Assert.Equal($"Short {MainWindow.AgentWroteGlyph}", label);
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
        => SetTabStateProperty(window, tab, "UserTitle", title);

    private static void SetTabBell(MainWindow window, TabItem tab, bool hasBell)
        => SetTabStateProperty(window, tab, "HasBell", hasBell);

    private static void SetTabActivity(MainWindow window, TabItem tab, bool hasActivity)
        => SetTabStateProperty(window, tab, "HasActivity", hasActivity);

    private static void SetTabStateProperty(MainWindow window, TabItem tab, string property, object value)
    {
        var getOrCreateTabState = typeof(MainWindow)
            .GetMethod("GetOrCreateTabState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = getOrCreateTabState.Invoke(window, new object[] { tab })!;
        state.GetType().GetProperty(property)!.SetValue(state, value);
    }

    /// <summary>
    /// A second TabItem with no backing pane, wired only well enough for
    /// BuildTabDisplayLabels/UpdateTabVisuals to render it: a header built by
    /// the real ConfigureTabHeader (so it has the Border/TextBlock structure
    /// FirstTabLabel reads) and added to the live TabControl. Used only for
    /// the collision test, where a second colliding label is all that is
    /// needed — no real terminal session behind it.
    /// </summary>
    private static TabItem AddBareTab(MainWindow window, string title)
    {
        var tab = new TabItem();
        typeof(MainWindow)
            .GetMethod("ConfigureTabHeader", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab, title });

        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.Items.Add(tab);
        return tab;
    }
}
