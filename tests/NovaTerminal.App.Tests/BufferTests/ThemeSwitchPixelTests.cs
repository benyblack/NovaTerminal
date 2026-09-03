using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace NovaTerminal.Tests.BufferTests
{
    /// <summary>
    /// End-to-end pixel regression for the live theme switch: drives the REAL TerminalView →
    /// TerminalDrawOperation → Skia pipeline (not just the buffer), switches the theme between
    /// renders, and asserts the banner row's dominant pixel color follows the new theme. The
    /// buffer-level migration tests in ThemeSwitchStaleScrollbackTests pass while the screen
    /// still shows old-theme boxes, so this is the test that guards what the user actually sees.
    ///
    /// Sample bands are derived from the laid-out TerminalView geometry (row height =
    /// view height / buffer rows) rather than fixed fractions of the host control, so pane
    /// chrome and layout changes cannot silently shift what is being measured (Greptile P2).
    /// </summary>
    public sealed class ThemeSwitchPixelTests
    {
        private static TerminalTheme LoadTheme(string fileName)
        {
            string outputThemes = Path.Combine(AppContext.BaseDirectory, "themes");
            string path = Path.Combine(outputThemes, fileName + ".json");
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme)
                   ?? throw new InvalidOperationException($"Could not load theme {fileName}");
        }

        private static TerminalSettings SettingsFor(TerminalTheme theme)
        {
            // ThemeName must match the assigned ActiveTheme, or the ActiveTheme getter
            // re-resolves through ThemeManager and hands back the stock Default theme.
            return new TerminalSettings
            {
                FontFamily = "Consolas",
                FontSize = 14,
                WindowOpacity = 1.0,
                ThemeName = theme.Name,
                ActiveTheme = theme
            };
        }

        private static TerminalBuffer BuildBufferWithBanner(TerminalTheme theme, int rows = 6)
        {
            var buffer = new TerminalBuffer(80, rows) { Theme = theme };
            var parser = new AnsiParser(buffer);
            parser.Process("Clink v1.9.25.dc17e7\r\nActive code page: 65001\r\n");
            for (int i = 0; i < rows - 3; i++)
            {
                parser.Process($"filler {i}\r\n");
            }

            return buffer;
        }

        /// <summary>
        /// Rasterizes <paramref name="rendered"/> inside its window and returns the dominant
        /// color of the absolute-pixel row band [yStart, yEnd).
        /// </summary>
        private static Color BandDominant(Control rendered, int yStart, int yEnd, out string diagnostics)
        {
            using var target = new RenderTargetBitmap(
                new PixelSize((int)rendered.Bounds.Width, (int)rendered.Bounds.Height), new Vector(96, 96));
            target.Render(rendered);

            using var stream = new MemoryStream();
            target.Save(stream);
            stream.Position = 0;
            using SKBitmap bitmap = SKBitmap.Decode(stream)
                ?? throw new InvalidOperationException("headless raster decode failed");

            var counts = new System.Collections.Generic.Dictionary<Color, int>();
            for (int y = yStart; y < Math.Min(yEnd, bitmap.Height); y++)
            {
                for (int x = 8; x < bitmap.Width - 8; x++)
                {
                    var p = bitmap.GetPixel(x, y);
                    var color = Color.FromRgb(p.Red, p.Green, p.Blue);
                    counts[color] = counts.GetValueOrDefault(color) + 1;
                }
            }

            // Whole-frame dominant colors, for diagnosing "nothing rendered" failures.
            var frameCounts = new System.Collections.Generic.Dictionary<string, int>();
            for (int y = 0; y < bitmap.Height; y += 3)
            {
                for (int x = 0; x < bitmap.Width; x += 3)
                {
                    var p = bitmap.GetPixel(x, y);
                    string key = $"{p.Red:X2}{p.Green:X2}{p.Blue:X2}{p.Alpha:X2}";
                    frameCounts[key] = frameCounts.GetValueOrDefault(key) + 1;
                }
            }

            diagnostics = "frame colors: " + string.Join(", ", frameCounts
                .OrderByDescending(kvp => kvp.Value)
                .Take(4)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));

            return counts.OrderByDescending(kvp => kvp.Value).First().Key;
        }

        [AvaloniaFact]
        public void ThemeSwitch_BannerRowPixels_FollowTheNewTheme()
        {
            var solarized = LoadTheme("SolarizedDark");
            var cobalt = LoadTheme("Cobalt2");

            var view = new TerminalView();
            view.SetBuffer(BuildBufferWithBanner(solarized));
            view.ApplySettings(SettingsFor(solarized));

            var window = new Window { Content = view, Width = 680, Height = 220 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();

                Assert.True(view.Bounds.Height > 0, $"TerminalView arranged to {view.Bounds}.");
                double rowHeight = view.Bounds.Height / 6.0;
                int bannerY1 = (int)(rowHeight * 0.15);
                int bannerY2 = (int)(rowHeight * 0.85);

                Color before = BandDominant(view, bannerY1, bannerY2, out _);
                Assert.Equal(solarized.Background.ToAvaloniaColor(), before);

                // The user's exact action: live theme switch on an existing pane.
                view.ApplySettings(SettingsFor(cobalt));
                Dispatcher.UIThread.RunJobs();

                Color after = BandDominant(view, bannerY1, bannerY2, out var diag);
                Assert.True(
                    after == cobalt.Background.ToAvaloniaColor(),
                    $"[view-level] After switching to Cobalt2 the banner row paints {after}, " +
                    $"expected {cobalt.Background.ToAvaloniaColor()} (pre-switch {before}). [{diag}]");
            }
            finally
            {
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        }

        [AvaloniaFact]
        public void ThemeSwitch_ThroughRealTerminalPane_BannerRowPixelsFollowTheNewTheme()
        {
            var solarized = LoadTheme("SolarizedDark");
            var cobalt = LoadTheme("Cobalt2");

            using var pane = new NovaTerminal.Controls.TerminalPane();
            pane.CreateAndWireParser();
            pane.Parser!.Process("Clink v1.9.25.dc17e7\r\nActive code page: 65001\r\n");
            for (int i = 0; i < 3; i++)
            {
                pane.Parser.Process($"filler {i}\r\n");
            }

            pane.ApplySettings(SettingsFor(solarized));

            var window = new Window { Content = pane, Width = 680, Height = 280 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();

                // Sample bands come from the laid-out TerminalView geometry and the live cell
                // metrics, translated into pane coordinates - not fixed fractions of the pane,
                // so pane chrome and layout changes cannot shift what is measured.
                var view = Assert.IsType<TerminalView>(pane.ActiveControl);
                var origin = view.TranslatePoint(new Point(0, 0), pane)
                             ?? throw new InvalidOperationException("TerminalView is not attached to the pane.");
                double cellHeight = view.Metrics.CellHeight > 0
                    ? view.Metrics.CellHeight
                    : view.Bounds.Height / 6.0;
                int bannerY1 = (int)(origin.Y + cellHeight * 0.2);
                int bannerY2 = (int)(origin.Y + cellHeight * 0.9);
                int fillerY1 = (int)(origin.Y + cellHeight * 2.3);
                int fillerY2 = (int)(origin.Y + cellHeight * 3.2);

                pane.ApplySettings(SettingsFor(cobalt));
                Dispatcher.UIThread.RunJobs();

                Color banner = BandDominant(pane, bannerY1, bannerY2, out var diag);
                Color filler = BandDominant(pane, fillerY1, fillerY2, out _);

                Assert.True(
                    banner == filler,
                    $"[pane-level] After switching to Cobalt2 the banner row paints {banner} but " +
                    $"the filler rows paint {filler} - the banner is still in the previous theme. [{diag}]");
            }
            finally
            {
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        }
    }
}
