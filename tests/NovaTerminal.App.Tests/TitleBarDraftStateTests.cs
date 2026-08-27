using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarDraftStateTests
    {
        private static TitleBarDraftState SeededFromDefaults()
        {
            var draft = new TitleBarDraftState();
            draft.SeedFrom(TitleBarLayoutResolver.Resolve(null, null, null));
            return draft;
        }

        [Fact]
        public void SeedFrom_MatchesCatalogDefaultsForEachEntry()
        {
            var draft = SeededFromDefaults();

            Assert.Equal(TitleBarItemState.Pinned, draft.GetState("new_tab"));
            Assert.Equal(TitleBarItemState.Pinned, draft.GetState("open_tab_list"));
            Assert.Equal(TitleBarItemState.Pinned, draft.GetState("connections"));
            Assert.Equal(TitleBarItemState.Pinned, draft.GetState("settings"));
            Assert.Equal(TitleBarItemState.Overflow, draft.GetState("find"));
            Assert.Equal(TitleBarItemState.Overflow, draft.GetState("toggle_recording"));
            Assert.Equal(TitleBarItemState.Hidden, draft.GetState("agent_activity"));
            Assert.Equal(4, draft.CountPinned());
        }

        [Fact]
        public void SeedFrom_SetsPinnedOrderFromTheLayout()
        {
            var draft = SeededFromDefaults();

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                draft.BuildSaveOrder());
        }

        [Fact]
        public void TrySetState_PinningAnOverflowEntry_Succeeds()
        {
            var draft = SeededFromDefaults();

            bool accepted = draft.TrySetState("find", TitleBarItemState.Pinned);

            Assert.True(accepted);
            Assert.Equal(TitleBarItemState.Pinned, draft.GetState("find"));
            Assert.Equal(5, draft.CountPinned());
            // Newly pinned entries land at the end of the pinned order.
            Assert.Equal("find", draft.BuildSaveOrder().Last());
        }

        [Fact]
        public void TrySetState_UnpinningAPinnedEntry_RemovesItFromTheOrder()
        {
            var draft = SeededFromDefaults();

            bool accepted = draft.TrySetState("open_tab_list", TitleBarItemState.Overflow);

            Assert.True(accepted);
            Assert.Equal(TitleBarItemState.Overflow, draft.GetState("open_tab_list"));
            Assert.DoesNotContain("open_tab_list", draft.BuildSaveOrder());
            Assert.Equal(3, draft.CountPinned());
        }

        [Fact]
        public void TrySetState_RejectsThePinAtMaxPinned_CountingTheLockedEntry()
        {
            var draft = SeededFromDefaults();

            // Defaults already pin the locked new_tab plus 3 unlocked entries (4 total). Pin four
            // more unlocked, currently-Overflow entries to reach MaxPinned (8) including the lock.
            Assert.True(draft.TrySetState("toggle_recording", TitleBarItemState.Pinned));
            Assert.True(draft.TrySetState("command_palette", TitleBarItemState.Pinned));
            Assert.True(draft.TrySetState("find", TitleBarItemState.Pinned));
            Assert.True(draft.TrySetState("split_vertical", TitleBarItemState.Pinned));
            Assert.Equal(TitleBarCatalog.MaxPinned, draft.CountPinned());

            // The 9th pin (split_horizontal) must be rejected: the cap counts the locked new_tab
            // entry, so 7 unlocked pins plus the lock already exhausts it.
            bool accepted = draft.TrySetState("split_horizontal", TitleBarItemState.Pinned);

            Assert.False(accepted);
            Assert.Equal(TitleBarItemState.Overflow, draft.GetState("split_horizontal"));
            Assert.Equal(TitleBarCatalog.MaxPinned, draft.CountPinned());
        }

        [Fact]
        public void TrySetState_ToOverflowOrHidden_IsNeverRejectedByTheCap()
        {
            var draft = SeededFromDefaults();

            Assert.True(draft.TrySetState("connections", TitleBarItemState.Overflow));
            Assert.True(draft.TrySetState("settings", TitleBarItemState.Hidden));
        }

        [Fact]
        public void MovePinned_Up_SwapsWithThePreviousEntry()
        {
            var draft = SeededFromDefaults();

            draft.MovePinned("connections", -1);

            Assert.Equal(
                new[] { "new_tab", "connections", "open_tab_list", "settings" },
                draft.BuildSaveOrder());
        }

        [Fact]
        public void MovePinned_Down_SwapsWithTheNextEntry()
        {
            var draft = SeededFromDefaults();

            draft.MovePinned("connections", +1);

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "settings", "connections" },
                draft.BuildSaveOrder());
        }

        [Fact]
        public void MovePinned_CannotMoveTheLockedIndexZeroEntry()
        {
            var draft = SeededFromDefaults();

            draft.MovePinned("new_tab", -1);
            draft.MovePinned("new_tab", +1);

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                draft.BuildSaveOrder());
        }

        [Fact]
        public void MovePinned_CannotDisplaceTheLockedIndexZeroEntry()
        {
            var draft = SeededFromDefaults();

            // Moving the second entry up would swap it into index 0, displacing the lock.
            draft.MovePinned("open_tab_list", -1);

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                draft.BuildSaveOrder());
        }

        [Fact]
        public void MovePinned_IgnoresAnEntryThatIsNotCurrentlyPinned()
        {
            var draft = SeededFromDefaults();

            draft.MovePinned("find", -1);

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                draft.BuildSaveOrder());
        }

        [Fact]
        public void BuildSaveDelta_IsEmpty_WhenNothingWasChangedFromCatalogDefaults()
        {
            var draft = SeededFromDefaults();

            Assert.Empty(draft.BuildSaveDelta());
        }

        [Fact]
        public void BuildSaveDelta_ContainsExactlyTheChangedIds()
        {
            var draft = SeededFromDefaults();

            draft.TrySetState("find", TitleBarItemState.Pinned);
            draft.TrySetState("open_tab_list", TitleBarItemState.Hidden);

            var delta = draft.BuildSaveDelta();

            Assert.Equal(2, delta.Count);
            Assert.Equal("Pinned", delta["find"]);
            Assert.Equal("Hidden", delta["open_tab_list"]);
        }

        [Fact]
        public void BuildSaveDelta_OmitsTheLockedEntryEvenIfItsStateChanged()
        {
            var draft = SeededFromDefaults();

            // The UI never offers a control to change the locked entry, but the delta computation
            // itself is what guarantees it is never persisted, regardless.
            draft.TrySetState("new_tab", TitleBarItemState.Overflow);

            Assert.DoesNotContain("new_tab", draft.BuildSaveDelta().Keys);
        }

        [Fact]
        public void BuildSaveOrder_ContainsOnlyCurrentlyPinnedIds()
        {
            var draft = SeededFromDefaults();

            draft.TrySetState("find", TitleBarItemState.Pinned);
            draft.TrySetState("open_tab_list", TitleBarItemState.Overflow);

            var order = draft.BuildSaveOrder();

            Assert.All(order, id => Assert.Equal(TitleBarItemState.Pinned, draft.GetState(id)));
            Assert.Equal(TitleBarCatalog.GetEntries().Count(e => draft.GetState(e.Id) == TitleBarItemState.Pinned), order.Count);
            Assert.Contains("find", order);
            Assert.DoesNotContain("open_tab_list", order);
        }

        [Fact]
        public void GetDisplayOrder_ListsPinnedFirstThenTheRestInCatalogOrder()
        {
            var draft = SeededFromDefaults();

            var displayOrder = draft.GetDisplayOrder();

            // Pinned entries (in pinned order) come first...
            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                displayOrder.Take(4));
            // ...followed by every other catalog id, in catalog order.
            var expectedTail = TitleBarCatalog.GetEntries()
                .Select(e => e.Id)
                .Where(id => draft.GetState(id) != TitleBarItemState.Pinned);
            Assert.Equal(expectedTail, displayOrder.Skip(4));
        }
    }
}
