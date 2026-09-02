using NovaTerminal.AgentHost;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Full matrix for the pure vertical-header presentation rules: which marker chips
    /// are visible (ResolveTabMarkers) and which single state the status dot paints
    /// (ResolveTabDot). Plain facts, no window (same split as TabDragModelTests).
    /// </summary>
    public sealed class TabStatusPresentationTests
    {
        private static TabMarkerSet Markers(
            bool bell = false, bool activity = false, bool wrote = false, bool watched = false)
            => new(bell, activity, wrote, watched);

        // ---- ResolveTabMarkers: bell/activity exclusivity ----

        [Fact]
        public void ResolveTabMarkers_NoSignals_AllFalse()
            => Assert.Equal(Markers(), TabStatusPresentation.ResolveTabMarkers(
                hasBell: false, hasActivity: false, AgentAttentionTier.Idle, rollupPolicy: "WritesOnly"));

        [Fact]
        public void ResolveTabMarkers_BellOnly_ShowsBellNotActivity()
            => Assert.Equal(Markers(bell: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: true, hasActivity: false, AgentAttentionTier.Idle, rollupPolicy: "WritesOnly"));

        [Fact]
        public void ResolveTabMarkers_ActivityOnly_ShowsActivity()
            => Assert.Equal(Markers(activity: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: false, hasActivity: true, AgentAttentionTier.Idle, rollupPolicy: "WritesOnly"));

        [Fact]
        public void ResolveTabMarkers_BellWinsOverActivity()
            => Assert.Equal(Markers(bell: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: true, hasActivity: true, AgentAttentionTier.Idle, rollupPolicy: "WritesOnly"));

        // ---- ResolveTabMarkers: tier x rollup policy ----

        [Theory]
        [InlineData("WritesOnly")]
        [InlineData("All")]
        [InlineData(null)]
        [InlineData("garbage")]
        public void ResolveTabMarkers_WroteTier_AlwaysShows_RegardlessOfPolicy(string? policy)
            => Assert.Equal(Markers(wrote: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: false, hasActivity: false, AgentAttentionTier.Wrote, rollupPolicy: policy));

        [Theory]
        [InlineData("WritesOnly")]
        [InlineData(null)]
        [InlineData("garbage")]
        public void ResolveTabMarkers_WatchedTier_HiddenUnderNonAllPolicies(string? policy)
            => Assert.Equal(Markers(), TabStatusPresentation.ResolveTabMarkers(
                hasBell: false, hasActivity: false, AgentAttentionTier.Watched, rollupPolicy: policy));

        [Fact]
        public void ResolveTabMarkers_WatchedTier_ShownUnderPolicyAll()
            => Assert.Equal(Markers(watched: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: false, hasActivity: false, AgentAttentionTier.Watched, rollupPolicy: "All"));

        [Fact]
        public void ResolveTabMarkers_TierIndependentOfBellAndActivity()
            => Assert.Equal(Markers(bell: true, wrote: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: true, hasActivity: true, AgentAttentionTier.Wrote, rollupPolicy: "WritesOnly"));

        [Fact]
        public void ResolveTabMarkers_WatchedWithBellAndAllPolicy_AllThreeMarkers()
            => Assert.Equal(Markers(bell: true, watched: true), TabStatusPresentation.ResolveTabMarkers(
                hasBell: true, hasActivity: false, AgentAttentionTier.Watched, rollupPolicy: "All"));

        // ---- Parity with MainWindow's copy of the rollup-policy rule ----

        // ResolveTabMarkers re-implements the same rollup-policy gate that
        // MainWindow.ShouldShowTierInTabStrip applies when RefreshTabAgentAttention filters
        // each tab's stored tier, so the two rules must agree on every policy input for the
        // Watched tier — a divergence would let a tab show the read chip (or paint the
        // watched dot) for a tier the strip-level rule says was filtered out, or hide a read
        // the rollup setting says should surface. Both are deliberately Ordinal "All"
        // (case-sensitive, so "all" hides reads); this theory pins that one shared decision
        // across both copies so neither can drift silently.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("WritesOnly")]
        [InlineData("All")]
        [InlineData("all")]
        [InlineData("garbage")]
        public void ResolveTabMarkers_WatchedTier_AgreesWithMainWindowTabStripRule(string? policy)
            => Assert.Equal(
                NovaTerminal.MainWindow.ShouldShowTierInTabStrip(policy, AgentAttentionTier.Watched),
                TabStatusPresentation.ResolveTabMarkers(
                    hasBell: false, hasActivity: false, AgentAttentionTier.Watched, rollupPolicy: policy).AgentWatched);

        // ---- ResolveTabDot: precedence, highest first ----

        [Fact]
        public void ResolveTabDot_IdleTabNoMarkers_NoDot()
            => Assert.Equal(TabDotVisual.None, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(), hasRunningCommand: false));

        [Fact]
        public void ResolveTabDot_AttentionStatus_BeatsEverything()
        {
            Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Attention, Markers(), hasRunningCommand: false));
            Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Attention, Markers(wrote: true, watched: true), hasRunningCommand: true));
        }

        [Fact]
        public void ResolveTabDot_BellMarker_SameAsAttention()
            => Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(bell: true), hasRunningCommand: false));

        [Fact]
        public void ResolveTabDot_AgentWrote_BeatsWorkingAndWatched()
        {
            Assert.Equal(TabDotVisual.AgentWrote, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(wrote: true), hasRunningCommand: false));
            Assert.Equal(TabDotVisual.AgentWrote, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Working, Markers(wrote: true), hasRunningCommand: true));
            Assert.Equal(TabDotVisual.AgentWrote, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(wrote: true, watched: true), hasRunningCommand: false));
        }

        [Fact]
        public void ResolveTabDot_AgentWrote_LosesOnlyToAttention()
            => Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(bell: true, wrote: true), hasRunningCommand: false));

        [Fact]
        public void ResolveTabDot_WorkingStatus_PaintsWorking()
            => Assert.Equal(TabDotVisual.Working, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Working, Markers(), hasRunningCommand: false));

        [Fact]
        public void ResolveTabDot_RunningCommand_FlipsNoneToWorking()
        {
            Assert.Equal(TabDotVisual.None, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(), hasRunningCommand: false));
            Assert.Equal(TabDotVisual.Working, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(), hasRunningCommand: true));
        }

        [Fact]
        public void ResolveTabDot_RunningCommand_DoesNotOverrideAttentionOrAgentWrote()
        {
            Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Attention, Markers(), hasRunningCommand: true));
            Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(bell: true), hasRunningCommand: true));
            Assert.Equal(TabDotVisual.AgentWrote, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(wrote: true), hasRunningCommand: true));
        }

        [Fact]
        public void ResolveTabDot_AgentWatched_LowestPrecedence()
        {
            // Alone it paints...
            Assert.Equal(TabDotVisual.AgentWatched, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(watched: true), hasRunningCommand: false));
            // ...but loses to working (status or running command), agent write, and attention.
            Assert.Equal(TabDotVisual.Working, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Working, Markers(watched: true), hasRunningCommand: false));
            Assert.Equal(TabDotVisual.Working, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(watched: true), hasRunningCommand: true));
            Assert.Equal(TabDotVisual.AgentWrote, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Idle, Markers(wrote: true, watched: true), hasRunningCommand: false));
            Assert.Equal(TabDotVisual.Attention, TabStatusPresentation.ResolveTabDot(
                TabTrackerStatus.Attention, Markers(watched: true), hasRunningCommand: false));
        }
    }
}
