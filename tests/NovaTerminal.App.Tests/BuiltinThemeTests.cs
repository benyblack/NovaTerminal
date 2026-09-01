using NovaTerminal.Shell;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary>
        /// Enumerates whatever theme files ship rather than a hand-maintained list, so a newly
        /// added theme is covered without remembering to update this test.
        /// </summary>
        public static IEnumerable<object[]> AllBuiltinThemeFiles =>
            Directory.EnumerateFiles(FindThemesDirectory(), "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new object[] { name! });

        [Theory]
        [MemberData(nameof(AllBuiltinThemeFiles))]
        public void BuiltinTheme_DeserializesWithFullySpecifiedColors(string fileName)
        {
            string path = Path.Combine(FindThemesDirectory(), fileName + ".json");

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

        /// <summary>
        /// The per-file theory above covers whatever ships; this pins the canonical set so a
        /// theme deleted by accident (or a broken content glob) still fails the build.
        /// </summary>
        [Fact]
        public void BuiltinThemes_CanonicalSetIsShipped()
        {
            string[] expected =
            [
                "Cobalt2", "CatppuccinMocha", "Dracula", "GitHubDark", "GitHubLight",
                "GruvboxDark", "Monokai", "Nord", "OneHalfDark", "OneHalfLight",
                "SolarizedDark", "SolarizedLight", "TokyoNight"
            ];
            var shipped = Directory.EnumerateFiles(FindThemesDirectory(), "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = expected.Where(name => !shipped.Contains(name)).ToList();
            Assert.True(missing.Count == 0, $"Missing built-in themes: {string.Join(", ", missing)}");
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
            // The app project's content glob copies themes into referencing projects' output;
            // prefer that so the test does not require running inside a source checkout.
            string outputThemes = Path.Combine(AppContext.BaseDirectory, "themes");
            if (Directory.Exists(outputThemes) && Directory.GetFiles(outputThemes, "*.json").Length > 0)
            {
                return outputThemes;
            }

            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "NovaTerminal.App", "themes");
                if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.json").Length > 0)
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate built-in theme files from test output path.");
        }
    }
}
