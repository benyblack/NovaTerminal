using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;
using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// The agent segment shares the pane status bar with SSH port forwards.
/// Two independent features, one bar: neither may erase the other, and only
/// the persistent layer may change the bar's visibility (a visibility change
/// resizes the terminal, so an agent read must never cause one).
/// </summary>
public class PaneAgentStatusBarTests
{
    [AvaloniaFact]
    public void A_non_actable_local_pane_shows_no_status_bar()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);

        Assert.False(GetStatusBar(pane).IsVisible);
    }

    [AvaloniaFact]
    public void An_actable_pane_shows_the_bar_with_the_baseline_segment()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.True(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void Activity_does_not_change_the_bars_visibility()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);
        Assert.False(GetStatusBar(pane).IsVisible);

        // A read arriving on a non-actable pane must not summon the bar: that
        // would take 22px from the terminal and fire a PTY resize.
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Watched, null, null), isActable: false);

        Assert.False(GetStatusBar(pane).IsVisible);
    }

    [AvaloniaFact]
    public void The_wrote_tier_names_the_method()
    {
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(
            new AgentAttentionSnapshot(AgentAttentionTier.Wrote, DateTimeOffset.UtcNow, "sendInput"),
            isActable: true);

        Assert.Contains("typed", GetAgentSegmentText(pane), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void An_ssh_pane_with_forwards_shows_the_bar_without_an_agent_segment()
    {
        // SSH-only: the bar exists for port forwards, but a pane that is not
        // agent-actable must not claim it is.
        using var pane = MakeSshPaneWithForward();
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.False(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void An_actable_ssh_pane_with_forwards_shows_both()
    {
        // UpdateForwardingStatus (the SSH label writer) only runs off a
        // 2-second DispatcherTimer tick in production; there is no headless
        // clock to advance in a unit test, so the fixture drives the same
        // private method the timer would have called (see
        // UpdateForwardingStatusForTesting). Without this the label assertion
        // below could never pass, regardless of the agent-attention code.
        using var pane = MakeSshPaneWithForward();
        pane.UpdateForwardingStatusForTesting();
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.False(string.IsNullOrEmpty(GetStatusBarLabel(pane)));
        Assert.True(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void An_ssh_forward_refresh_does_not_erase_the_agent_segment()
    {
        // UpdateStatusBarUI clears StatusBarRules wholesale; the agent segment
        // lives in its own container precisely so it survives that.
        //
        // This has to be an SSH pane with forwards, driven through
        // UpdateForwardingStatus. The earlier version of this test built a local
        // pane and called UpdateStatusBarVisibility(), which never touches
        // StatusBarRules at all — UpdateStatusBarUI was never reached, so it
        // would have passed even with the segment nested back inside
        // StatusBarRules, the exact regression it exists to prevent.
        using var pane = MakeSshPaneWithForward();
        pane.ApplyAgentAttention(
            new AgentAttentionSnapshot(AgentAttentionTier.Wrote, DateTimeOffset.UtcNow, "sendInput"),
            isActable: true);
        Assert.True(GetAgentSegment(pane).IsVisible);

        // UpdateForwardingStatus only rebuilds the bar when a rule's status
        // actually changed or the bar was hidden, and here the bar is already
        // up. Degraded is a status the recompute never produces (it yields
        // Active/Starting/Stopped only), so the change — and therefore the
        // UpdateStatusBarUI call — is guaranteed, whatever is listening locally.
        pane.Profile!.Forwards[0].Status = ForwardingStatus.Degraded;
        pane.UpdateForwardingStatusForTesting();

        // Proof the wholesale-clearing path really ran: UpdateStatusBarUI is the
        // only thing that populates StatusBarRules.
        Assert.Single(GetStatusBarRules(pane).Children);

        Assert.True(GetAgentSegment(pane).IsVisible);
        Assert.Contains("typed", GetAgentSegmentText(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Color.Parse("#F0C07A"), GetAgentSegmentTextColor(pane));
    }

    [AvaloniaFact]
    public void The_agent_segment_is_a_clickable_button()
    {
        // The design promises "clicking the agent segment opens the existing
        // Agent Activity window". The pane segment is the surface the user
        // actually looks at, so leaving it inert while the window-level light is
        // actionable is backwards. Its visibility is still owned solely by
        // UpdateStatusBarVisibility, alongside the panel inside it.
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);
        Assert.False(GetAgentButton(pane).IsVisible);

        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);

        var button = GetAgentButton(pane);
        Assert.True(button.IsVisible);
        Assert.Same(GetAgentSegment(pane), button.Content);
        // Never steal keyboard focus from the terminal.
        Assert.False(button.Focusable);
    }

    [AvaloniaFact]
    public void Clicking_the_segment_outside_a_window_is_harmless()
    {
        // The handler resolves VisualRoot as MainWindow; a pane that is not
        // attached to one (every unit test, and any pane mid-construction) must
        // no-op rather than throw.
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);

        GetAgentButton(pane).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(GetAgentButton(pane).IsVisible);
    }

    [AvaloniaFact]
    public void The_tooltip_names_the_tier()
    {
        using var pane = new TerminalPane("cmd.exe");

        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: true);
        Assert.Contains("can read", GetAgentTooltip(pane), StringComparison.OrdinalIgnoreCase);

        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Watched, null, null), isActable: true);
        Assert.Contains("is reading", GetAgentTooltip(pane), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void The_wrote_tooltip_names_the_write_time_and_method()
    {
        // "agent typed" is sticky for at least ten seconds, so the two-word
        // label alone cannot distinguish a write from a moment ago from one the
        // user already saw. The tooltip carries the clock time and the method.
        using var pane = new TerminalPane("cmd.exe");
        var writtenAt = DateTimeOffset.UtcNow;

        pane.ApplyAgentAttention(
            new AgentAttentionSnapshot(AgentAttentionTier.Wrote, writtenAt, "closeSession"),
            isActable: true);

        string tip = GetAgentTooltip(pane);
        Assert.Contains(writtenAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), tip, StringComparison.Ordinal);
        Assert.Contains("closeSession", tip, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_actability_flip_with_no_tier_transition_surfaces_the_bar()
    {
        // The gap this fixes: AgentHostService.RefreshActability writes
        // IsAgentActable directly (global act toggle flipped, or an SSH
        // profile added to the allowlist) with no tier transition alongside
        // it. Before the fix, nothing told an already-open idle pane to
        // re-render, so the bar never appeared. No NoteRead/NoteWrote/tier
        // change anywhere in this test — only the actability flip.
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(new AgentAttentionSnapshot(AgentAttentionTier.Idle, null, null), isActable: false);
        Assert.False(GetStatusBar(pane).IsVisible);

        var registration = pane.AgentRegistrationForTesting;
        Assert.NotNull(registration);
        registration!.IsAgentActable = true; // the exact setter RefreshActability writes through
        Dispatcher.UIThread.RunJobs(); // flush the Dispatcher.UIThread.Post the event handler queues

        Assert.True(GetStatusBar(pane).IsVisible);
        Assert.True(GetAgentSegment(pane).IsVisible);
    }

    [AvaloniaFact]
    public void Repeated_identical_actability_writes_raise_the_event_once()
    {
        // The storm guard: AgentHostService.RefreshActability runs from a 1 s
        // sweep and writes IsAgentActable unconditionally on every tick, for
        // every registration. If the setter raised on every write rather than
        // only on an actual change, this would become a perpetual
        // once-per-second Dispatcher.UIThread.Post per pane.
        using var pane = new TerminalPane("cmd.exe");
        var registration = pane.AgentRegistrationForTesting;
        Assert.NotNull(registration);

        int raiseCount = 0;
        registration!.ActabilityChanged += _ => raiseCount++;

        registration.IsAgentActable = true;
        registration.IsAgentActable = true; // same value, as a sweep tick would write
        registration.IsAgentActable = true;

        Assert.Equal(1, raiseCount);

        registration.IsAgentActable = false; // an actual change still raises
        Assert.Equal(2, raiseCount);
    }

    // Neighbouring pane tests reach controls with FindControl<T> (see
    // tests/NovaTerminal.App.Tests/Controls/PaneAssistInsertionTests.cs:850),
    // which is nullable — assert the type rather than dereferencing blind.
    private static Border GetStatusBar(TerminalPane pane)
        => Assert.IsType<Border>(pane.FindControl<Border>("StatusBar"));

    private static StackPanel GetAgentSegment(TerminalPane pane)
        => Assert.IsType<StackPanel>(pane.FindControl<StackPanel>("AgentStatusSegment"));

    private static string GetAgentSegmentText(TerminalPane pane)
        => Assert.IsType<TextBlock>(pane.FindControl<TextBlock>("AgentStatusText")).Text ?? string.Empty;

    private static Color GetAgentSegmentTextColor(TerminalPane pane)
        => Assert.IsType<SolidColorBrush>(
            Assert.IsType<TextBlock>(pane.FindControl<TextBlock>("AgentStatusText")).Foreground).Color;

    private static Button GetAgentButton(TerminalPane pane)
        => Assert.IsType<Button>(pane.FindControl<Button>("AgentStatusButton"));

    private static string GetAgentTooltip(TerminalPane pane)
        => ToolTip.GetTip(GetAgentButton(pane)) as string ?? string.Empty;

    private static StackPanel GetStatusBarRules(TerminalPane pane)
        => Assert.IsType<StackPanel>(pane.FindControl<StackPanel>("StatusBarRules"));

    private static string GetStatusBarLabel(TerminalPane pane)
        => Assert.IsType<TextBlock>(pane.FindControl<TextBlock>("StatusBarLabel")).Text ?? string.Empty;

    // An SSH pane with one local forward, so the SSH half of the visibility OR
    // is exercised. NOTE the type: TerminalPane's profile ctor takes
    // NovaTerminal.Shell.TerminalProfile — NOT the Platform-layer SshProfile.
    // TerminalProfile.Forwards is List<ForwardingRule>; SshProfile.Forwards is
    // List<PortForward> and is a different thing entirely. No real session is
    // started: the status bar only reads Profile.Forwards.
    private static TerminalPane MakeSshPaneWithForward()
    {
        var profile = new TerminalProfile
        {
            Name = "host",
            Type = ConnectionType.SSH,
        };
        profile.Forwards.Add(new ForwardingRule
        {
            Type = ForwardingType.Local,
            LocalAddress = "8080",
            RemoteAddress = "localhost:80",
        });
        return new TerminalPane(profile, SshDiagnosticsLevel.None);
    }
}
