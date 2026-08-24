using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarLayoutResolverTests
    {
        private static TitleBarLayout Resolve(
            Dictionary<string, string>? states = null,
            List<string>? order = null,
            params string[] activeToggles)
            => TitleBarLayoutResolver.Resolve(states, order, activeToggles.ToHashSet());

        private static IEnumerable<string> Ids(IEnumerable<TitleBarCatalogEntry> entries)
            => entries.Select(e => e.Id);

        [Fact]
        public void EmptySettings_YieldsCatalogDefaults()
        {
            var layout = Resolve();

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                Ids(layout.Pinned));
            Assert.True(layout.ShowOverflowButton);
            Assert.DoesNotContain("agent_activity", Ids(layout.Overflow));
        }

        [Fact]
        public void NullSettings_YieldsCatalogDefaults()
        {
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            Assert.Equal(4, layout.Pinned.Count);
        }

        [Fact]
        public void UnknownIdInSettings_IsIgnored()
        {
            var layout = Resolve(new() { ["not_a_real_action"] = "Pinned" });

            Assert.Equal(4, layout.Pinned.Count);
            Assert.DoesNotContain("not_a_real_action", Ids(layout.Pinned));
        }

        [Fact]
        public void EntryAbsentFromSettings_UsesItsDefaultState()
        {
            var layout = Resolve(new() { ["find"] = "Pinned" });

            Assert.Contains("find", Ids(layout.Pinned));
            Assert.Contains("command_palette", Ids(layout.Overflow));
        }

        [Fact]
        public void UnparseableStateString_FallsBackToDefault()
        {
            var layout = Resolve(new() { ["find"] = "banana" });

            Assert.Contains("find", Ids(layout.Overflow));
            Assert.DoesNotContain("find", Ids(layout.Pinned));
        }

        [Fact]
        public void StateValue_IsCaseInsensitive()
        {
            var layout = Resolve(new() { ["find"] = "pinned" });

            Assert.Contains("find", Ids(layout.Pinned));
        }

        [Fact]
        public void StateKey_IsCaseInsensitive()
        {
            var layout = Resolve(new() { ["Find"] = "Pinned" });

            Assert.Contains("find", Ids(layout.Pinned));
        }

        [Fact]
        public void ActiveToggleId_IsCaseInsensitive()
        {
            var layout = Resolve(activeToggles: "Toggle_Recording");

            Assert.Equal("toggle_recording", layout.Pinned[^1].Id);
            Assert.DoesNotContain("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void StateKeys_DifferingOnlyByCase_DoNotThrow_AndLastOneWins()
        {
            // Both "find" and "Find" are ordinal-distinct, so a hand-edited settings.json can
            // contain both as sibling keys and deserialize cleanly into a case-sensitive
            // Dictionary<string, string>. Normalizing that dictionary to OrdinalIgnoreCase must
            // not throw on the collision; per the documented last-wins rule, whichever key was
            // enumerated last (here, "Find") determines the resolved state.
            var layout = Resolve(new() { ["find"] = "Overflow", ["Find"] = "Pinned" });

            Assert.Contains("find", Ids(layout.Pinned));
            Assert.DoesNotContain("find", Ids(layout.Overflow));
        }

        [Fact]
        public void HiddenEntry_AppearsInNeitherList()
        {
            var layout = Resolve(new() { ["connections"] = "Hidden" });

            Assert.DoesNotContain("connections", Ids(layout.Pinned));
            Assert.DoesNotContain("connections", Ids(layout.Overflow));
        }

        [Fact]
        public void ExplicitOrder_IsHonored_AndUnnamedEntriesFollowInCatalogOrder()
        {
            var layout = Resolve(
                new() { ["find"] = "Pinned" },
                new List<string> { "settings", "connections" });

            // new_tab is locked and leads; then the named ids in their given order;
            // then the remaining pinned entries in catalog order.
            Assert.Equal(
                new[] { "new_tab", "settings", "connections", "open_tab_list", "find" },
                Ids(layout.Pinned));
        }

        [Fact]
        public void OrderNamingUnknownOrNonPinnedIds_IgnoresThem()
        {
            var layout = Resolve(
                order: new List<string> { "nope", "agent_activity", "settings" });

            Assert.Equal(
                new[] { "new_tab", "settings", "open_tab_list", "connections" },
                Ids(layout.Pinned));
        }

        [Fact]
        public void LockedEntry_LeadsEvenWhenOrderPutsItLast()
        {
            var layout = Resolve(
                order: new List<string> { "settings", "connections", "open_tab_list", "new_tab" });

            Assert.Equal("new_tab", layout.Pinned[0].Id);
        }

        [Fact]
        public void LockedEntry_StaysPinned_WhenSettingsTryToHideIt()
        {
            var layout = Resolve(new() { ["new_tab"] = "Hidden" });

            Assert.Contains("new_tab", Ids(layout.Pinned));
            Assert.DoesNotContain("new_tab", Ids(layout.Overflow));
        }

        [Fact]
        public void LockedEntry_StaysPinned_WhenSettingsMoveItToOverflow()
        {
            var layout = Resolve(new() { ["new_tab"] = "Overflow" });

            Assert.Contains("new_tab", Ids(layout.Pinned));
            Assert.DoesNotContain("new_tab", Ids(layout.Overflow));
        }

        [Fact]
        public void ActiveToggle_IsPromotedOutOfOverflow_ToTheEndOfPinned()
        {
            var layout = Resolve(activeToggles: "toggle_recording");

            Assert.Equal("toggle_recording", layout.Pinned[^1].Id);
            Assert.DoesNotContain("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void InactiveToggle_StaysInOverflow()
        {
            var layout = Resolve();

            Assert.Contains("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void ActiveToggle_ThatIsHidden_IsNotPromoted()
        {
            var layout = Resolve(
                new() { ["toggle_recording"] = "Hidden" },
                activeToggles: "toggle_recording");

            Assert.DoesNotContain("toggle_recording", Ids(layout.Pinned));
            Assert.DoesNotContain("toggle_recording", Ids(layout.Overflow));
        }

        [Fact]
        public void ActiveToggle_AlreadyPinned_IsNotDuplicated()
        {
            var layout = Resolve(
                new() { ["toggle_recording"] = "Pinned" },
                activeToggles: "toggle_recording");

            Assert.Single(layout.Pinned, e => e.Id == "toggle_recording");
        }

        [Fact]
        public void ShowOverflowButton_IsFalse_WhenNothingIsInOverflow()
        {
            var states = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, _ => "Hidden");

            var layout = Resolve(states);

            Assert.False(layout.ShowOverflowButton);
            Assert.Empty(layout.Overflow);
        }

        [Fact]
        public void ShowOverflowButton_IsFalse_WhenTheOnlyOverflowEntryIsAutoSurfaced()
        {
            var states = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, e => e.Id == "toggle_recording" ? "Overflow" : "Hidden");

            var layout = Resolve(states, activeToggles: "toggle_recording");

            Assert.False(layout.ShowOverflowButton);
            Assert.Contains("toggle_recording", Ids(layout.Pinned));
        }

        [Fact]
        public void Resolve_DoesNotClampPinnedCount()
        {
            var states = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, _ => "Pinned");

            var layout = Resolve(states);

            Assert.Equal(TitleBarCatalog.GetEntries().Count, layout.Pinned.Count);
            Assert.True(layout.Pinned.Count > TitleBarCatalog.MaxPinned);
        }
    }
}
