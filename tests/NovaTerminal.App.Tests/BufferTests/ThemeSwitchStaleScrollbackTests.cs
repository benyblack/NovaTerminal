using NovaTerminal.Shell;
using System;
using System.IO;
using System.Text.Json;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.BufferTests
{
    /// <summary>
    /// Reproduces the "stale text after a live theme switch" report: a plain (default-attribute)
    /// banner printed under one theme scrolls into paged scrollback, the theme is switched, and
    /// the render snapshot the draw operation consumes must describe those rows with the NEW
    /// theme's colors. The user-visible symptom was dark boxes of the OLD theme background
    /// clinging to scrolled banner text after switching e.g. Solarized Dark → GitHub Light,
    /// while fresh tabs (which snapshot rows for the first time) looked correct.
    ///
    /// Two variants on purpose: without a pre-switch snapshot (isolates the cell migration) and
    /// with one (the real running-app flow - the paged-row snapshot cache is already populated
    /// with old-theme colors when the switch happens).
    /// </summary>
    public class ThemeSwitchStaleScrollbackTests
    {
        private static TerminalTheme LoadTheme(string fileName)
        {
            string path = Path.Combine(FindThemesDirectory(), fileName + ".json");
            string json = File.ReadAllText(path);
            var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);
            Assert.NotNull(theme);
            return theme!;
        }

        private static string FindThemesDirectory()
        {
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

        /// <summary>6-row viewport; the banner line and 4 fillers end up in paged scrollback.</summary>
        private static TerminalBuffer BuildBufferWithScrolledBanner(TerminalTheme theme)
        {
            var buffer = new TerminalBuffer(80, 6) { Theme = theme };
            var parser = new AnsiParser(buffer);

            parser.Process("Clink v1.9.25.dc17e7\r\n");
            for (int i = 0; i < 9; i++)
            {
                parser.Process($"filler {i}\r\n");
            }

            Assert.True(buffer.TotalLines >= 10, "expected the banner to have scrolled into scrollback");
            return buffer;
        }

        private static RenderCellSnapshot[] FindBannerRowCells(TerminalRenderSnapshot snapshot)
        {
            for (int r = 0; r < snapshot.RowsData.Length; r++)
            {
                var row = snapshot.RowsData.Array![r];
                if (row.Cells.Length > 2 && row.Cells[0].Character == 'C' && row.Cells[1].Character == 'l')
                {
                    return row.Cells;
                }
            }

            throw new Xunit.Sdk.XunitException("banner row not found in the snapshot");
        }

        private static void AssertBannerRowUsesNewTheme(TerminalBuffer buffer, TerminalTheme newTheme, bool populateSnapshotCacheFirst)
        {
            var oldTheme = buffer.Theme;

            if (populateSnapshotCacheFirst)
            {
                buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
                {
                    ViewportRows = 6,
                    ViewportCols = 80,
                    ScrollOffset = 5
                }, out _);
            }

            buffer.Theme = newTheme;
            buffer.UpdateThemeColors(oldTheme);

            var after = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = 6,
                ViewportCols = 80,
                ScrollOffset = 5
            }, out _);

            var cells = FindBannerRowCells(after);
            Assert.True(cells[0].IsDefaultBackground, "plain banner text must keep the default-background flag");
            Assert.Equal(newTheme.Background, cells[0].Background);
        }

        [Fact]
        public void ScrollbackRow_NoPriorSnapshot_CarriesNewThemeColorsAfterSwitch()
        {
            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");
            var buffer = BuildBufferWithScrolledBanner(oldTheme);

            AssertBannerRowUsesNewTheme(buffer, newTheme, populateSnapshotCacheFirst: false);
        }

        [Fact]
        public void ScrollbackRow_SnapshotCachedBeforeSwitch_CarriesNewThemeColorsAfterSwitch()
        {
            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");
            var buffer = BuildBufferWithScrolledBanner(oldTheme);

            AssertBannerRowUsesNewTheme(buffer, newTheme, populateSnapshotCacheFirst: true);
        }

        [Fact]
        public void ScrollbackRow_MaterializedDefaultColors_AreAdoptedIntoTheNewTheme()
        {
            // The "materialized default" shape: cells store the old theme's default colors with
            // the default flags cleared. They render exactly like default cells under the old
            // theme, so a switch must adopt them - otherwise scrolled history keeps old-theme
            // boxes while everything around it follows the new theme.
            var pool = new NovaTerminal.VT.Storage.TerminalPagePool();
            int cols = 10;
            var scrollback = new NovaTerminal.VT.Storage.ScrollbackPages(cols, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");

            var row = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                row[i] = new TerminalCell(
                    'x', oldTheme.Foreground, oldTheme.Background,
                    isDefaultFg: false, isDefaultBg: false);
            }

            scrollback.AppendRow(row.AsSpan());
            scrollback.UpdateThemeDefaults(oldTheme, newTheme);

            var updated = scrollback.GetRow(0);
            Assert.True(updated[0].IsDefaultForeground);
            Assert.True(updated[0].IsDefaultBackground);
            Assert.Equal(newTheme.Foreground.ToUint(), updated[0].Fg);
            Assert.Equal(newTheme.Background.ToUint(), updated[0].Bg);

            pool.Clear();
        }

        [Fact]
        public void ViewportBanner_OnScreenAcrossSwitch_CarriesNewThemeColors()
        {
            // The report also shows stale boxes when the banner never left the live screen:
            // banner on the viewport, snapshot cached, then a theme switch.
            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");
            var buffer = new TerminalBuffer(80, 6) { Theme = oldTheme };
            var parser = new AnsiParser(buffer);
            parser.Process("Clink v1.9.25.dc17e7\r\nActive code page: 65001");

            // Populate every per-row cache with pre-switch colors.
            buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = 6,
                ViewportCols = 80,
                ScrollOffset = 0
            }, out _);

            buffer.Theme = newTheme;
            buffer.UpdateThemeColors(oldTheme);

            var after = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = 6,
                ViewportCols = 80,
                ScrollOffset = 0
            }, out _);

            var cells = FindBannerRowCells(after);
            Assert.True(cells[0].IsDefaultBackground, "plain banner text must keep the default-background flag");
            Assert.Equal(newTheme.Background, cells[0].Background);
        }

        [Fact]
        public void ThemeSwitch_TwiceInARow_NeverLagsOneThemeBehind()
        {
            // "One switch behind" is the user-visible signature of a stale cache: the banner
            // shows the PREVIOUS theme until the next switch. Cycle through three themes and
            // assert the snapshot always matches the newest one.
            var first = LoadTheme("SolarizedDark");
            var second = LoadTheme("GitHubLight");
            var third = LoadTheme("Cobalt2");
            var buffer = BuildBufferWithScrolledBanner(first);

            var previous = first;
            foreach (var next in new[] { second, third, second })
            {
                buffer.Theme = next;
                buffer.UpdateThemeColors(previous);

                var after = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
                {
                    ViewportRows = 6,
                    ViewportCols = 80,
                    ScrollOffset = 5
                }, out _);

                var cells = FindBannerRowCells(after);
                Assert.Equal(next.Background, cells[0].Background);
                previous = next;
            }
        }
    }
}
