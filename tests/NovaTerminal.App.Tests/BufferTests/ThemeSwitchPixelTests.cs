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

        /// <summary>
        /// Renders the view and returns the most frequent color inside the horizontal band
        /// [yStartFraction, yEndFraction) of the control's height.
        /// </summary>
        private static Color RenderBandDominantColor(Control view, int width, int height, double yStartFraction, double yEndFraction, out string diagnostics)
        {
            var window = new Window { Content = view, Width = width + 40, Height = height + 40 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();

                Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0,
                    $"The view arranged to {view.Bounds}; nothing to rasterize.");

                using var target = new RenderTargetBitmap(
                    new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
                target.Render(view);

                using var stream = new MemoryStream();
                target.Save(stream);
                stream.Position = 0;
                using SKBitmap bitmap = SKBitmap.Decode(stream)
                    ?? throw new InvalidOperationException("headless raster decode failed");

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
                    .Take(5)
                    .Select(kvp => $"#{kvp.Key}={kvp.Value}"));

                int yStart = (int)(bitmap.Height * yStartFraction);
                int yEnd = Math.Max(yStart + 4, (int)(bitmap.Height * yEndFraction));
                var counts = new System.Collections.Generic.Dictionary<Color, int>();
                for (int y = yStart; y < yEnd; y++)
                {
                    for (int x = 8; x < bitmap.Width - 8; x++)
                    {
                        var p = bitmap.GetPixel(x, y);
                        var color = Color.FromRgb(p.Red, p.Green, p.Blue);
                        counts[color] = counts.GetValueOrDefault(color) + 1;
                    }
                }

                return counts.OrderByDescending(kvp => kvp.Value).First().Key;
            }
            finally
            {
                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        }

        [AvaloniaFact]
        public void ThemeSwitch_BannerRowPixels_FollowTheNewTheme()
        {
            var solarized = LoadTheme("SolarizedDark");
            var cobalt = LoadTheme("Cobalt2");

            var buffer = new TerminalBuffer(80, 6) { Theme = solarized };
            var parser = new AnsiParser(buffer);
            parser.Process("Clink v1.9.25.dc17e7\r\nActive code page: 65001\r\n");
            for (int i = 0; i < 3; i++)
            {
                parser.Process($"filler {i}\r\n");
            }

            var view = new TerminalView();
            view.SetBuffer(buffer);
            view.ApplySettings(SettingsFor(solarized));

            Color before = RenderBandDominantColor(view, 640, 180, 0.0, 0.16, out var diagBefore);

            // The user's exact action: live theme switch on an existing pane.
            view.ApplySettings(SettingsFor(cobalt));

            Color after = RenderBandDominantColor(view, 640, 180, 0.0, 0.16, out var diagAfter);

            Assert.True(
                after == cobalt.Background.ToAvaloniaColor(),
                $"[view-level] After switching to Cobalt2 the banner row still paints {after} " +
                $"(expected {cobalt.Background.ToAvaloniaColor()}). " +
                $"Pre-switch it was {before} with [{diagBefore}]; post-switch [{diagAfter}].");
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
            Color before = RenderBandDominantColor(pane, 640, 240, 0.05, 0.15, out var diagBefore);

            pane.ApplySettings(SettingsFor(cobalt));
            Color after = RenderBandDominantColor(pane, 640, 240, 0.05, 0.15, out var diagAfter);

            // The pane composites its own chrome over the terminal, so compare the banner band
            // against a band from the filler rows rendered in the SAME frame: after a correct
            // switch both bands share one background (the new theme's), while the reported bug
            // left the banner band stuck in the previous theme's colors.
            Color filler = RenderBandDominantColor(pane, 640, 240, 0.30, 0.42, out _);

            Assert.True(
                after == filler,
                $"[pane-level] After switching to Cobalt2 the banner row paints {after} but the " +
                $"filler rows paint {filler} - the banner is still in the previous theme. " +
                $"Pre-switch the banner was {before} with [{diagBefore}]; post-switch [{diagAfter}].");
        }
    }
}
