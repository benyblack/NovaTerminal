using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NovaTerminal.Shell;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests
{
    public class TitleBarSettingsRoundTripTests
    {
        [Fact]
        public void NewSettings_HaveEmptyTitleBarConfig()
        {
            var settings = new TerminalSettings();

            Assert.NotNull(settings.TitleBarItems);
            Assert.Empty(settings.TitleBarItems);
            Assert.NotNull(settings.TitleBarOrder);
            Assert.Empty(settings.TitleBarOrder);
        }

        [Fact]
        public void TitleBarConfig_SurvivesAJsonRoundTrip()
        {
            var settings = new TerminalSettings
            {
                TitleBarItems = new Dictionary<string, string>
                {
                    ["find"] = "Pinned",
                    ["toggle_recording"] = "Hidden",
                },
                TitleBarOrder = ["settings", "find"],
            };

            string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
            var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings);

            Assert.NotNull(restored);
            Assert.Equal("Pinned", restored!.TitleBarItems["find"]);
            Assert.Equal("Hidden", restored.TitleBarItems["toggle_recording"]);
            Assert.Equal(new[] { "settings", "find" }, restored.TitleBarOrder);
        }

        [Fact]
        public void SettingsJsonWithNoTitleBarKeys_DeserializesToCatalogDefaults()
        {
            // A settings file written before this feature shipped.
            var restored = JsonSerializer.Deserialize(
                """{"FontSize":14}""",
                AppJsonContext.Default.TerminalSettings);

            Assert.NotNull(restored);
            var layout = TitleBarLayoutResolver.Resolve(
                restored!.TitleBarItems, restored.TitleBarOrder, null);

            Assert.Equal(
                new[] { "new_tab", "open_tab_list", "connections", "settings" },
                layout.Pinned.Select(e => e.Id));
        }

        [Fact]
        public void RoundTrippedConfig_ResolvesToTheSameLayout()
        {
            var settings = new TerminalSettings
            {
                TitleBarItems = new Dictionary<string, string> { ["find"] = "Pinned" },
                TitleBarOrder = ["find", "settings"],
            };

            string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.TerminalSettings);
            var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings)!;

            var before = TitleBarLayoutResolver.Resolve(settings.TitleBarItems, settings.TitleBarOrder, null);
            var after = TitleBarLayoutResolver.Resolve(restored.TitleBarItems, restored.TitleBarOrder, null);

            Assert.Equal(before.Pinned.Select(e => e.Id), after.Pinned.Select(e => e.Id));
            Assert.Equal(before.Overflow.Select(e => e.Id), after.Overflow.Select(e => e.Id));
        }
    }
}
