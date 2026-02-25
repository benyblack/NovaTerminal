using SkiaSharp;
using NovaTerminal.Core;
using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace NovaTerminal.Tests.Infra
{
    public class SnapshotService
    {
        public static SKBitmap Capture(TerminalBuffer buffer, CellMetrics metrics, int width, int height)
        {
            var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                // We recreate the state of TerminalDrawOperation manually for the snapshot
                var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
                var glyphTypeface = typeface.GlyphTypeface;
                var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
                var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));

                var op = new TerminalDrawOperation(
                    new Rect(0, 0, width, height),
                    buffer,
                    0, // scrollOffset
                    new SelectionState(),
                    null, // searchMatches
                    -1, // activeSearchIndex
                    metrics,
                    typeface,
                    14, // fontSize
                    glyphTypeface,
                    skTypeface,
                    skFont,
                    false, // enableLigatures
                    new ConcurrentDictionary<string, SKTypeface?>(),
                    new SKTypeface[0], // fallbackChain
                    1.0, // opacity
                    false, // transparent
                    false, // hideCursor
                    1.0, // renderScaling
                    buffer.Rows,
                    buffer.Cols,
                    buffer.TotalLines,
                    buffer.CursorRow,
                    buffer.CursorCol
                );

                // Use reflection to call the private DrawTerminal method 
                // OR we can just use the public Render if we had an ImmediateDrawingContext (harder to mock)
                // Let's use reflection for the test utility to access the core drawing logic directly.
                var method = typeof(TerminalDrawOperation).GetMethod("DrawTerminal",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                method?.Invoke(op, new object[] { canvas });

                op.Dispose();
            }
            return bitmap;
        }

        public static bool Compare(SKBitmap expected, SKBitmap actual, float tolerance = 0.01f)
        {
            if (expected.Width != actual.Width || expected.Height != actual.Height)
                return false;

            int diffPixels = 0;
            int totalPixels = expected.Width * expected.Height;

            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                {
                    var c1 = expected.GetPixel(x, y);
                    var c2 = actual.GetPixel(x, y);

                    if (c1 != c2)
                    {
                        // Check if the difference is within individual color channel tolerance
                        // (useful for anti-aliasing variations)
                        if (Math.Abs(c1.Red - c2.Red) > 2 ||
                            Math.Abs(c1.Green - c2.Green) > 2 ||
                            Math.Abs(c1.Blue - c2.Blue) > 2)
                        {
                            diffPixels++;
                        }
                    }
                }
            }

            return (float)diffPixels / totalPixels <= tolerance;
        }

        public static void SaveBaseline(SKBitmap bitmap, string name)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Baselines", $"{name}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }

        public static SKBitmap? LoadBaseline(string name)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Baselines", $"{name}.png");
            if (!File.Exists(path)) return null;
            return SKBitmap.Decode(path);
        }
    }
}
