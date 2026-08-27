using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarViewFactoryTests
    {
        private static IReadOnlyDictionary<string, Action> AllHandlers(List<string>? invoked = null)
            => TitleBarCatalog.GetEntries().ToDictionary(
                e => e.Id,
                e => new Action(() => invoked?.Add(e.Id)));

        [Fact]
        public void Resolve_UsesTheShortcutCatalogDefault_WhenNoOverride()
        {
            Assert.Equal("Ctrl+,", TitleBarShortcuts.Resolve("settings", null));
        }

        [Fact]
        public void Resolve_PrefersTheUserOverride()
        {
            var keybindings = new Dictionary<string, string> { ["settings"] = "Ctrl+Alt+S" };

            Assert.Equal("Ctrl+Alt+S", TitleBarShortcuts.Resolve("settings", keybindings));
        }

        [Fact]
        public void Resolve_IgnoresAWhitespaceOverride()
        {
            var keybindings = new Dictionary<string, string> { ["settings"] = "   " };

            Assert.Equal("Ctrl+,", TitleBarShortcuts.Resolve("settings", keybindings));
        }

        [Fact]
        public void Resolve_ReturnsEmpty_ForAnActionWithNoShortcutKey()
        {
            Assert.Equal(string.Empty, TitleBarShortcuts.Resolve("", null));
        }

        [Fact]
        public void Resolve_ReturnsEmpty_ForAnUnknownShortcutKey()
        {
            Assert.Equal(string.Empty, TitleBarShortcuts.Resolve("no_such_command", null));
        }

        [Fact]
        public void FormatTooltip_AppendsTheShortcutInParentheses()
        {
            Assert.Equal("Settings (Ctrl+,)", TitleBarShortcuts.FormatTooltip("Settings", "Ctrl+,"));
        }

        [Fact]
        public void FormatTooltip_OmitsTheParenthesesWhenUnbound()
        {
            Assert.Equal("Transfers", TitleBarShortcuts.FormatTooltip("Transfers", ""));
        }

        [AvaloniaFact]
        public void Populate_AddsOneButtonPerPinnedEntry_PlusTheOverflowButton()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Equal(layout.Pinned.Count + 1, host.Children.Count);
            Assert.Equal(
                TitleBarViewFactory.OverflowButtonName,
                (host.Children[^1] as Button)?.Name);
        }

        [AvaloniaFact]
        public void Populate_NamesEachButtonAfterItsCatalogId()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Equal(
                layout.Pinned.Select(e => TitleBarViewFactory.ButtonName(e.Id)),
                host.Children.Take(layout.Pinned.Count).Select(c => (c as Button)?.Name));
        }

        [AvaloniaFact]
        public void Populate_OmitsTheOverflowButton_WhenNothingIsInOverflow()
        {
            var host = new StackPanel();
            var states = TitleBarCatalog.GetEntries()
                .ToDictionary(e => e.Id, e => e.IsLocked ? "Pinned" : "Hidden");
            var layout = TitleBarLayoutResolver.Resolve(states, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Single(host.Children);
            Assert.DoesNotContain(
                TitleBarViewFactory.OverflowButtonName,
                host.Children.Select(c => (c as Button)?.Name));
        }

        [AvaloniaFact]
        public void Populate_IsIdempotent_AcrossRepeatedCalls()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });
            int first = host.Children.Count;
            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.Equal(first, host.Children.Count);
        }

        [AvaloniaFact]
        public void Populate_ReusesTheSuppliedNewTabButton_SoItsFlyoutSurvives()
        {
            var host = new StackPanel();
            var newTab = new Button { Name = "BtnNewTab", Content = "+" };
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), newTab, _ => { });

            Assert.Same(newTab, host.Children[0]);
        }

        [AvaloniaFact]
        public void Populate_ReusesTheSuppliedNewTabButton_SetsTheResolvedTooltip()
        {
            var host = new StackPanel();
            // The XAML-declared button ships with a static ToolTip.Tip="New Tab" that carries no
            // shortcut; Populate must overwrite it with the same title-plus-shortcut format every
            // generated button gets, not leave the stale XAML value in place.
            var newTab = new Button { Name = "BtnNewTab", Content = "+" };
            ToolTip.SetTip(newTab, "New Tab");
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), newTab, _ => { });

            Assert.Equal("New Tab (Ctrl+Shift+T)", ToolTip.GetTip(newTab));
        }

        [AvaloniaFact]
        public void Populate_ReusesTheSuppliedNewTabButton_TooltipReflectsAUserShortcutOverride()
        {
            var host = new StackPanel();
            var newTab = new Button { Name = "BtnNewTab", Content = "+" };
            var keybindings = new Dictionary<string, string> { ["new_tab"] = "Ctrl+Alt+T" };
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, keybindings, AllHandlers(), newTab, _ => { });

            Assert.Equal("New Tab (Ctrl+Alt+T)", ToolTip.GetTip(newTab));
        }

        [AvaloniaFact]
        public void Populate_ReusesTheSameNewTabButton_AcrossRepeatedCalls_WithoutThrowing()
        {
            var host = new StackPanel();
            var newTab = new Button { Name = "BtnNewTab", Content = "+" };
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            // Mirrors Task 5's real usage: a settings change re-populates the same host with the
            // same XAML-declared newTabButton reference passed again. Populate must remove it from
            // its previous parent before re-adding, or Avalonia throws on the second call.
            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), newTab, _ => { });
            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), newTab, _ => { });

            Assert.Same(newTab, host.Children[0]);
        }

        [AvaloniaFact]
        public void Populate_ClickingAButton_InvokesItsHandler()
        {
            var host = new StackPanel();
            var invoked = new List<string>();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(invoked), null, _ => { });

            var settingsButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.ButtonName("settings"));
            settingsButton.Command?.Execute(null);

            Assert.Equal(new[] { "settings" }, invoked);
        }

        [AvaloniaFact]
        public void Populate_ReportsAndSkips_APinnedEntryWithNoHandler()
        {
            var host = new StackPanel();
            var handlers = AllHandlers().Where(kv => kv.Key != "settings")
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var missing = new List<string>();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, handlers, null, missing.Add);

            Assert.Equal(new[] { "settings" }, missing);
            Assert.DoesNotContain(
                TitleBarViewFactory.ButtonName("settings"),
                host.Children.Select(c => (c as Button)?.Name));
        }

        [AvaloniaFact]
        public void Populate_PutsEveryOverflowEntryInTheFlyout()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            var overflowButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.OverflowButtonName);
            var flyout = Assert.IsType<MenuFlyout>(overflowButton.Flyout);

            Assert.Equal(layout.Overflow.Count, flyout.Items.Count);
        }

        [AvaloniaFact]
        public void Populate_SuppressesTheOverflowButton_WhenEveryOverflowHandlerIsMissing()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);
            var overflowIds = layout.Overflow.Select(e => e.Id).ToList();
            var handlers = AllHandlers()
                .Where(kv => !overflowIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var missing = new List<string>();

            // Every overflow entry's handler is missing: this is a wiring bug, and
            // ShowOverflowButton alone can't see it, since it only knows the layout, not the
            // handler wiring. The factory must still not render a button that opens an empty menu.
            TitleBarViewFactory.Populate(host, layout, null, handlers, null, missing.Add);

            Assert.DoesNotContain(
                TitleBarViewFactory.OverflowButtonName,
                host.Children.Select(c => (c as Button)?.Name));
            Assert.Equal(overflowIds, missing);
        }

        [AvaloniaFact]
        public void Populate_RendersOverflowButton_WithOnlyTheResolvedEntries_WhenOneHandlerIsMissing()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);
            string missingId = layout.Overflow[0].Id;
            var handlers = AllHandlers()
                .Where(kv => kv.Key != missingId)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var missing = new List<string>();

            TitleBarViewFactory.Populate(host, layout, null, handlers, null, missing.Add);

            var overflowButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.OverflowButtonName);
            var flyout = Assert.IsType<MenuFlyout>(overflowButton.Flyout);

            Assert.Equal(layout.Overflow.Count - 1, flyout.Items.Count);
            Assert.Equal(new[] { missingId }, missing);
        }

        [AvaloniaFact]
        public void Populate_TooltipsCarryTitleAndShortcut()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            var settingsButton = host.Children
                .OfType<Button>()
                .Single(b => b.Name == TitleBarViewFactory.ButtonName("settings"));

            Assert.Equal("Settings (Ctrl+,)", ToolTip.GetTip(settingsButton));
        }

        [AvaloniaFact]
        public void Populate_ButtonsAreNotFocusable_SoClicksNeverStealTerminalFocus()
        {
            var host = new StackPanel();
            var layout = TitleBarLayoutResolver.Resolve(null, null, null);

            TitleBarViewFactory.Populate(host, layout, null, AllHandlers(), null, _ => { });

            Assert.All(host.Children.OfType<Button>(), b => Assert.False(b.Focusable));
        }
    }
}
