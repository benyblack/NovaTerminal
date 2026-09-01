using NovaTerminal.Shell;
using System.IO;
using System.Text.Json;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// The built-in themes ship as JSON files under src/NovaTerminal.App/themes and are seeded
    /// into the user's theme directory by AppPaths migration. A malformed or misnamed file would
    /// fail silently (the loader logs and skips, the color converter falls back to Transparent),
    /// so the expectations are asserted here instead.
    /// </summary>
    public class BuiltinThemeTests
    {
        public static TheoryData<string> ThemeFileNames => new()
        {
            "Dracula", "GitHubDark", "GitHubLight", "Monokai", "Nord", "OneHalfDark",
            "OneHalfLight", "SolarizedDark", "SolarizedLight", "TokyoNight",
            "CatppuccinMocha", "GruvboxDark", "Cobalt2"
        };

        [Theory]
        [MemberData(nameof(ThemeFileNames))]
        public void BuiltinTheme_DeserializesWithFullySpecifiedColors(string fileName)
        {
            string path = Path.Combine(FindThemesDirectory(), fileName + ".json");
            Assert.True(File.Exists(path), $"Built-in theme file is missing: {path}");

            string json = File.ReadAllText(path);
            var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);

            Assert.NotNull(theme);
            Assert.Equal(fileName.Replace(" ", ""), theme!.Name.Replace(" ", ""));

            // A Transparent value means the hex string was missing or failed to parse.
            Assert.NotEqual(TermColor.Transparent, theme.Foreground);
            Assert.NotEqual(TermColor.Transparent, theme.Background);
            Assert.NotEqual(TermColor.Transparent, theme.CursorColor);
            for (int index = 0; index < 8; index++)
            {
                Assert.NotEqual(TermColor.Transparent, theme.GetAnsiColor(index, bright: false));
                Assert.NotEqual(TermColor.Transparent, theme.GetAnsiColor(index, bright: true));
            }
        }

        [Fact]
        public void Dracula_UsesOfficialForeground()
        {
            string path = Path.Combine(FindThemesDirectory(), "Dracula.json");
            var theme = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.TerminalTheme);

            Assert.NotNull(theme);
            // #FF5F1F shipped here by mistake once; Dracula's foreground is the soft white.
            Assert.Equal(new TermColor(0xF8, 0xF8, 0xF2), theme!.Foreground);
        }

        private static string FindThemesDirectory()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "NovaTerminal.App", "themes");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/NovaTerminal.App/themes from test output path.");
        }
    }
}
