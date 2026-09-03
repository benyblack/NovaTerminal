namespace NovaTerminal.Shell
{
    /// <summary>Which single calm state the vertical tab header's status dot paints.</summary>
    internal enum TabDotVisual
    {
        /// <summary>Nothing worth a dot: no attention, agent write, work, or watched tier.</summary>
        None,

        /// <summary>Output burst in flight (or a running command): the theme-blue dot.</summary>
        Working,

        /// <summary>Bell or attention status: the amber dot.</summary>
        Attention,

        /// <summary>An agent typed into the tab: the amber agent dot.</summary>
        AgentWrote,

        /// <summary>An agent is reading the tab (policy "All" only): the blue agent dot.</summary>
        AgentWatched,
    }

    /// <summary>
    /// The discrete attention markers a vertical tab header shows as trailing chips.
    /// Unlike the dot (exactly one <see cref="TabDotVisual"/>), several markers can be
    /// visible at once; only bell and plain activity are mutually exclusive.
    /// </summary>
    /// <param name="Bell">A bell fired on the tab and has not been acknowledged.</param>
    /// <param name="Activity">Recent output activity (never true when <paramref name="Bell"/> is).</param>
    /// <param name="AgentWrote">An agent typed into a pane in this tab, unacknowledged.</param>
    /// <param name="AgentWatched">An agent read a pane in this tab; only surfaced under the "All" rollup policy.</param>
    internal readonly record struct TabMarkerSet(bool Bell, bool Activity, bool AgentWrote, bool AgentWatched);

    /// <summary>
    /// Pure decision logic for the vertical tab header's agent-aware status presentation:
    /// which marker chips are visible and which single state the status dot paints. No
    /// Avalonia types so the tests stay plain [Fact]s (same split as TabDragModel /
    /// TabStripLayout); <c>MainWindow.UpdateVerticalTabExtras</c> turns these results
    /// into actual brushes and chip visibilities on the existing visual-refresh pass —
    /// nothing here runs per output tick.
    /// </summary>
    internal static class TabStatusPresentation
    {
        /// <summary>
        /// Resolves the marker chips for one tab from its raw inputs. Bell wins over
        /// plain activity (mirrors <c>MainWindow.GetAttentionMarkerSuffix</c>'s
        /// exclusivity); the agent tiers are independent of both. A watched tier only
        /// counts under rollup policy "All" — the same rule
        /// <c>MainWindow.ShouldShowTierInTabStrip</c> applies (a write always shows; an
        /// unknown or null policy behaves as "WritesOnly", hiding reads).
        /// </summary>
        /// <remarks>Callers pass the tab state's stored tier, which
        /// <c>MainWindow.RefreshTabAgentAttention</c> has already filtered through the
        /// same policy — re-checking here keeps the helper pure and correct on raw
        /// inputs, and is idempotent on already-filtered ones.</remarks>
        internal static TabMarkerSet ResolveTabMarkers(
            bool hasBell,
            bool hasActivity,
            AgentHost.AgentAttentionTier agentTier,
            string? rollupPolicy)
        {
            return new TabMarkerSet(
                Bell: hasBell,
                Activity: hasActivity && !hasBell,
                AgentWrote: agentTier == AgentHost.AgentAttentionTier.Wrote,
                AgentWatched: agentTier == AgentHost.AgentAttentionTier.Watched
                              && string.Equals(rollupPolicy, "All", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// Resolves the single dot visual (highest precedence first): attention (bell or
        /// attention status) beats an agent write, which beats working output (or a
        /// running command), which beats a watched tier. The header shows exactly one
        /// calm dot so an idle-glancing user is never asked to parse a colored cluster.
        /// </summary>
        internal static TabDotVisual ResolveTabDot(TabTrackerStatus status, TabMarkerSet markers, bool hasRunningCommand)
        {
            if (status == TabTrackerStatus.Attention || markers.Bell)
            {
                return TabDotVisual.Attention;
            }

            if (markers.AgentWrote)
            {
                return TabDotVisual.AgentWrote;
            }

            if (status == TabTrackerStatus.Working || hasRunningCommand)
            {
                return TabDotVisual.Working;
            }

            if (markers.AgentWatched)
            {
                return TabDotVisual.AgentWatched;
            }

            return TabDotVisual.None;
        }
    }
}
