using NovaTerminal.AgentHost;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Which attention tiers reach the tab strip. Pure policy — no window, no
/// pane, no Avalonia — mirroring how ShellExitPolicyTests covers its sibling.
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
}
