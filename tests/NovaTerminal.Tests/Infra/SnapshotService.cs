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
                var typeface = new Typeface("Cascadia Code PL, CaskaydiaCove Nerd Font, Cascadia Code, Consolas, Monospace");
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

        // Use a heuristic to find the repo root so we write baselines into the source tree
        private static string GetRepoTestRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "tests", "NovaTerminal.Tests")))
                {
                    return Path.Combine(dir.FullName, "tests", "NovaTerminal.Tests");
                }
                dir = dir.Parent;
            }
            // Fallback
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public static void SaveBaseline(SKBitmap bitmap, string name)
        {
            string path = Path.Combine(GetRepoTestRoot(), "Baselines", $"{name}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }

        public static SKBitmap? LoadBaseline(string name)
        {
            string path = Path.Combine(GetRepoTestRoot(), "Baselines", $"{name}.png");
            if (!File.Exists(path)) return null;
            return SKBitmap.Decode(path);
        }

        public static void CompareToBaseline(string name, SKBitmap actual)
        {
            bool updateSnapshots = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";

            if (updateSnapshots)
            {
                SaveBaseline(actual, name);
                return;
            }

            var expected = LoadBaseline(name);
            if (expected == null)
            {
                throw new Xunit.Sdk.XunitException($"Baseline '{name}' is missing. Please run tests with UPDATE_SNAPSHOTS=1 to generate it.");
            }

            if (expected.Width != actual.Width || expected.Height != actual.Height || !Compare(expected, actual, tolerance: 0.001f))
            {
                // Write diffs to TestOutput
                string outputDir = Path.Combine(GetRepoTestRoot(), "TestOutput", "Diffs");
                Directory.CreateDirectory(outputDir);
                
                string cleanName = name.Replace("/", "_").Replace("\\", "_");
                string expectedPath = Path.Combine(outputDir, $"{cleanName}_expected.png");
                string actualPath = Path.Combine(outputDir, $"{cleanName}_actual.png");
                string diffPath = Path.Combine(outputDir, $"{cleanName}_diff.png");

                // Save Expected
                using (var expImage = SKImage.FromBitmap(expected))
                using (var expData = expImage.Encode(SKEncodedImageFormat.Png, 100))
                using (var expStream = File.OpenWrite(expectedPath))
                    expData.SaveTo(expStream);

                // Save Actual
                using (var actImage = SKImage.FromBitmap(actual))
                using (var actData = actImage.Encode(SKEncodedImageFormat.Png, 100))
                using (var actStream = File.OpenWrite(actualPath))
                    actData.SaveTo(actStream);

                // Save Diff
                using (var diffBitmap = GenerateDiff(expected, actual))
                using (var diffImage = SKImage.FromBitmap(diffBitmap))
                using (var diffData = diffImage.Encode(SKEncodedImageFormat.Png, 100))
                using (var diffStream = File.OpenWrite(diffPath))
                    diffData.SaveTo(diffStream);

                throw new Xunit.Sdk.XunitException(
                    $"Snapshot mismatch for '{name}'.\n" +
                    $"Sizes: Expected={expected.Width}x{expected.Height}, Actual={actual.Width}x{actual.Height}\n" +
                    $"Output saved to:\nExpected: {expectedPath}\nActual: {actualPath}\nDiff: {diffPath}");
            }
        }

        private static SKBitmap GenerateDiff(SKBitmap expected, SKBitmap actual)
        {
            int w = Math.Max(expected.Width, actual.Width);
            int h = Math.Max(expected.Height, actual.Height);
            
            var diff = new SKBitmap(w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool inExpected = x < expected.Width && y < expected.Height;
                    bool inActual = x < actual.Width && y < actual.Height;

                    if (inExpected && inActual)
                    {
                        var c1 = expected.GetPixel(x, y);
                        var c2 = actual.GetPixel(x, y);

                        if (Math.Abs(c1.Red - c2.Red) > 2 ||
                            Math.Abs(c1.Green - c2.Green) > 2 ||
                            Math.Abs(c1.Blue - c2.Blue) > 2)
                        {
                            // Highlight difference in red
                            diff.SetPixel(x, y, new SKColor(255, 0, 0, 255));
                        }
                        else
                        {
                            // Dim the common pixels
                            diff.SetPixel(x, y, new SKColor(c1.Red, c1.Green, c1.Blue, 64));
                        }
                    }
                    else if (inExpected)
                    {
                        diff.SetPixel(x, y, new SKColor(0, 0, 255, 255)); // Missing in actual is blue
                    }
                    else if (inActual)
                    {
                        diff.SetPixel(x, y, new SKColor(0, 255, 0, 255)); // Extra in actual is green
                    }
                }
            }
            return diff;
        }
    }
}
