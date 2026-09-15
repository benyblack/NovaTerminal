using System.Linq;
using NovaTerminal.Shell.Shortcuts;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarCatalogTests
    {
        [Fact]
        public void Ids_AreUniqueAndNonEmpty()
        {
            var entries = TitleBarCatalog.GetEntries();

            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Id)));
            Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        }

        [Fact]
        public void EveryEntry_HasTitleAndGeometry()
        {
            Assert.All(TitleBarCatalog.GetEntries(), e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Title));
                Assert.False(string.IsNullOrWhiteSpace(e.IconGeometry));
                Assert.True(e.IconSize > 0);
            });
        }

        [Fact]
        public void ExactlyOneEntry_IsLocked_AndItIsNewTab()
        {
            var locked = TitleBarCatalog.GetEntries().Where(e => e.IsLocked).ToList();

            Assert.Single(locked);
            Assert.Equal("new_tab", locked[0].Id);
        }

        [Fact]
        public void LockedEntry_DefaultsToPinned()
        {
            var locked = TitleBarCatalog.GetEntries().Single(e => e.IsLocked);

            Assert.Equal(TitleBarItemState.Pinned, locked.DefaultState);
        }

        [Fact]
        public void DefaultPinnedSet_IsTheFourAgreedEntries()
        {
            var pinned = TitleBarCatalog.GetEntries()
                .Where(e => e.DefaultState == TitleBarItemState.Pinned)
                .Select(e => e.Id)
                .ToList();

            Assert.Equal(new[] { "new_tab", "open_tab_list", "connections", "settings" }, pinned);
        }

        [Fact]
        public void ShortcutKeys_WhenPresent_ExistInShortcutCatalog()
        {
            var known = ShortcutCatalog.GetEntries().Select(e => e.CommandId).ToHashSet();

            Assert.All(
                TitleBarCatalog.GetEntries().Where(e => !string.IsNullOrEmpty(e.ShortcutKey)),
                e => Assert.Contains(e.ShortcutKey, known));
        }

        [Fact]
        public void ToggleEntries_AreOnlyRecording()
        {
            var toggles = TitleBarCatalog.GetEntries().Where(e => e.IsToggle).Select(e => e.Id).ToList();

            Assert.Equal(new[] { "toggle_recording" }, toggles);
        }
    }
}
