using NovaTerminal.Shell;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace NovaTerminal.Tests.RenderTests
{
    // Regression: when window transparency is enabled, an explicit background whose color is
    // identical to the theme background (e.g. a remote SGR "40" / erased line on a dark theme
    // where Background == ANSI black) must stay transparent, exactly like a default-background
    // cell. Otherwise it renders as an opaque island over the see-through theme layer — the
    // "solid block at the top of a fresh SSH session" bug.
    public class TransparentDefaultEqualBackgroundTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        [AvaloniaFact]
        public void ExplicitBackgroundEqualToThemeBackground_IsTransparent_UnderWindowOpacity()
        {
            const int cols = 40;
            const int rows = 3;
            const double opacity = 0.5;

            // Cols 0..7 get an explicit palette-0 (black) background that equals the theme
            // background; the rest of the row stays at the default background.
            using var bmp = Render("\x1b[40m        \x1b[49m", cols, rows, opacity);

            int y = (int)Math.Round(Metrics.CellHeight * 0.5);
            int explicitBgX = XForColCenter(2); // inside the SGR-40 run, over a space (no glyph)
            int defaultBgX = XForColCenter(20); // never-written default cell

            SKColor explicitBg = bmp.GetPixel(explicitBgX, y);
            SKColor defaultBg = bmp.GetPixel(defaultBgX, y);

            // The default cell shows the semi-transparent theme layer (~128 alpha).
            Assert.True(defaultBg.Alpha < 255,
                $"sanity: default-bg cell should be semi-transparent, got alpha={defaultBg.Alpha}");

            // The explicit default-equal cell must be just as transparent — not an opaque block.
            Assert.True(Math.Abs(explicitBg.Alpha - defaultBg.Alpha) <= 2,
                $"explicit bg equal to theme bg rendered opaque under transparency: " +
                $"explicit alpha={explicitBg.Alpha}, default alpha={defaultBg.Alpha}");
        }

        [AvaloniaFact]
        public void ExplicitBackgroundEqualToThemeBackground_StaysOpaque_WhenNotTransparent()
        {
            const int cols = 40;
            const int rows = 3;

            using var bmp = Render("\x1b[40m        \x1b[49m", cols, rows, opacity: 1.0);

            int y = (int)Math.Round(Metrics.CellHeight * 0.5);
            SKColor explicitBg = bmp.GetPixel(XForColCenter(2), y);

            // No transparency: everything is fully opaque (no behavior change for the common case).
            Assert.Equal(255, explicitBg.Alpha);
        }

        private static int XForColCenter(int col)
        {
            const double paddingLeft = 4.0;
            return (int)Math.Round(paddingLeft + ((col + 0.5) * Metrics.CellWidth));
        }

        private static SKBitmap Render(string text, int cols, int rows, double opacity)
        {
            int width = (int)Math.Ceiling((cols * Metrics.CellWidth) + 8);
            int height = (int)Math.Ceiling(rows * Metrics.CellHeight);

            var buffer = new TerminalBuffer(cols, rows)
            {
                Theme = new TerminalTheme
                {
                    Foreground = TermColor.White,
                    Background = TermColor.Black,
                    CursorColor = TermColor.White
                }
            };
            // Feed through the real ANSI parser so SGR sequences (e.g. "40m") are interpreted,
            // not written as literal characters.
            new AnsiParser(buffer).Process(text);

            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);

            var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
            var glyphTypeface = typeface.GlyphTypeface;
            var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
            var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));

            var op = new TerminalDrawOperation(
                new Rect(0, 0, width, height),
                buffer,
                scrollOffset: 0,
                selection: new SelectionState(),
                searchMatches: null,
                activeSearchIndex: -1,
                metrics: Metrics,
                typeface: typeface,
                fontSize: 14,
                glyphTypeface: glyphTypeface,
                skTypeface: skTypeface,
                skFont: skFont,
                enableLigatures: false,
                fallbackCache: new ConcurrentDictionary<string, SKTypeface?>(),
                fallbackChain: Array.Empty<SKTypeface>(),
                opacity: opacity,
                hideCursor: true,
                renderScaling: 1.0,
                snapshotRows: buffer.Rows,
                snapshotCols: buffer.Cols,
                totalLines: buffer.TotalLines,
                cursorRow: buffer.CursorRow,
                cursorCol: buffer.CursorCol,
                rowCache: null,
                enableComplexShaping: true,
                glyphCache: null);

            try
            {
                op.DrawTerminalInternal(canvas);
                return bitmap;
            }
            finally
            {
                op.Dispose();
                skFont.Dispose();
                skTypeface.Dispose();
            }
        }
    }
}
