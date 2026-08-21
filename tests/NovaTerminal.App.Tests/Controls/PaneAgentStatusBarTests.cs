using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
        using var pane = new TerminalPane("cmd.exe");
        pane.ApplyAgentAttention(
            new AgentAttentionSnapshot(AgentAttentionTier.Wrote, DateTimeOffset.UtcNow, "sendInput"),
            isActable: true);

        pane.UpdateStatusBarVisibility();

        Assert.True(GetAgentSegment(pane).IsVisible);
        Assert.Contains("typed", GetAgentSegmentText(pane), StringComparison.OrdinalIgnoreCase);
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
