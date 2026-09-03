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

        private static RenderCellSnapshot[] FindRowCells(TerminalRenderSnapshot snapshot, char firstChar, char secondChar)
        {
            var dump = new System.Text.StringBuilder();
            for (int r = 0; r < snapshot.RowsData.Length; r++)
            {
                var row = snapshot.RowsData.Array![r];
                if (row.Cells.Length > 2 && row.Cells[0].Character == firstChar && row.Cells[1].Character == secondChar)
                {
                    return row.Cells;
                }

                dump.Append($" row{r}='");
                for (int c = 0; c < Math.Min(12, row.Cells.Length); c++)
                {
                    dump.Append(row.Cells[c].Character == '\0' ? '~' : row.Cells[c].Character);
                }

                dump.Append('\'');
            }

            throw new Xunit.Sdk.XunitException(
                $"row starting with '{firstChar}{secondChar}' not found in the snapshot.{dump}");
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

            var cells = FindRowCells(after, 'C', 'l');
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
        public void ScrollbackRow_ExplicitTruecolorEqualToOldDefault_FollowsTheNewTheme()
        {
            // Paint-to-match adoption: shells and CLIs query OSC 11 and then explicitly paint
            // their prompt/status regions with the answered terminal background. Those cells
            // store a truecolor equal to the old default with the default flags cleared, and
            // after a theme switch they must keep matching the terminal (adopted into the new
            // default) instead of rendering as old-theme boxes. Reviewed in PR #388: value
            // matching is intentional here - see the OSC 11 note in UpdateThemeColors.
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
        public void ScrollbackRow_ExplicitTruecolorDifferentFromOldDefault_IsLeftAlone()
        {
            // The adoption must stay value-anchored to the OLD default: genuinely distinct
            // explicit colors keep their exact color across theme switches.
            var pool = new NovaTerminal.VT.Storage.TerminalPagePool();
            int cols = 10;
            var scrollback = new NovaTerminal.VT.Storage.ScrollbackPages(cols, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");

            var row = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                row[i] = new TerminalCell(
                    'x', TermColor.FromRgb(12, 34, 56), TermColor.FromRgb(200, 100, 50),
                    isDefaultFg: false, isDefaultBg: false);
            }

            scrollback.AppendRow(row.AsSpan());
            scrollback.UpdateThemeDefaults(oldTheme, newTheme);

            var updated = scrollback.GetRow(0);
            Assert.False(updated[0].IsDefaultForeground);
            Assert.False(updated[0].IsDefaultBackground);
            Assert.Equal(TermColor.FromRgb(12, 34, 56).ToUint(), updated[0].Fg);
            Assert.Equal(TermColor.FromRgb(200, 100, 50).ToUint(), updated[0].Bg);

            pool.Clear();
        }

        [Fact]
        public void ViewportRow_PaintToMatchPrompt_FollowsTheNewThemeAfterSwitch()
        {
            // The user-reported shape: a PowerShell prompt region painted by the shell with the
            // OSC 11 answered background (equal to the old theme's default). After a dark →
            // light switch the region must adopt the new theme's default instead of rendering
            // as a dark box.
            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");
            var buffer = new TerminalBuffer(80, 6) { Theme = oldTheme };
            var parser = new AnsiParser(buffer);

            // #002b36 == Solarized Dark's background, painted explicitly (OSC 11 match).
            parser.Process("\x1b[48;2;0;43;54mPS C:\\Users\\behna>\x1b[0m");

            buffer.Theme = newTheme;
            buffer.UpdateThemeColors(oldTheme);

            var after = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = 6,
                ViewportCols = 80,
                ScrollOffset = 0
            }, out _);

            var cells = FindRowCells(after, 'P', 'S');
            Assert.True(cells[0].IsDefaultBackground, "paint-to-match cells must be adopted as default background");
            Assert.Equal(newTheme.Background, cells[0].Background);
        }

        [Fact]
        public void ActivePaintToMatchSgr_AcrossSwitch_NextWriteUsesTheNewTheme()
        {
            // Greptile P1 on the paint-to-match adoption: a shell that KEEPS the OSC-derived
            // SGR active across the switch must not re-materialize the old color on its next
            // write - the live SGR state has to be adopted along with the cells.
            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");
            var buffer = new TerminalBuffer(80, 6) { Theme = oldTheme };
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[48;2;0;43;54mPS ");

            buffer.Theme = newTheme;
            buffer.UpdateThemeColors(oldTheme);
            parser.Process("C:\\Users\\behna>");

            var after = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = 6,
                ViewportCols = 80,
                ScrollOffset = 0
            }, out _);

            var cells = FindRowCells(after, 'P', 'S');
            for (int i = 0; i < 18; i++)
            {
                Assert.True(cells[i].IsDefaultBackground,
                    $"cell {i} ('{cells[i].Character}') kept a non-default background after the switch");
                Assert.Equal(newTheme.Background, cells[i].Background);
            }
        }

        [Fact]
        public void SavedCursor_WithPaintToMatchSgr_RestoresAdoptedStateAfterSwitch()
        {
            // The same gap for DECSC/DECRC: a saved cursor state holding the OSC-derived
            // background must restore as the adopted (new default) state, not the old color.
            // SaveCursor/RestoreCursor are driven directly so the test exercises the state
            // adoption rather than escape-sequence parsing.
            var oldTheme = LoadTheme("SolarizedDark");
            var newTheme = LoadTheme("GitHubLight");
            var buffer = new TerminalBuffer(80, 6) { Theme = oldTheme };
            var parser = new AnsiParser(buffer);

            buffer.SaveCursor();
            parser.Process("\x1b[48;2;0;43;54m");

            buffer.Theme = newTheme;
            buffer.UpdateThemeColors(oldTheme);
            buffer.RestoreCursor();
            parser.Process("restored");

            var after = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
            {
                ViewportRows = 6,
                ViewportCols = 80,
                ScrollOffset = 0
            }, out _);

            var cells = FindRowCells(after, 'r', 'e');
            Assert.True(cells[0].IsDefaultBackground);
            Assert.Equal(newTheme.Background, cells[0].Background);
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

            var cells = FindRowCells(after, 'C', 'l');
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

                var cells = FindRowCells(after, 'C', 'l');
                Assert.Equal(next.Background, cells[0].Background);
                previous = next;
            }
        }
    }
}
