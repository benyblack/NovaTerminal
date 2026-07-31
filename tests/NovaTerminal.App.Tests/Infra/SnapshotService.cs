using NovaTerminal.Shell;
using Avalonia;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using SkiaSharp;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace NovaTerminal.Tests.Infra
{
    public enum BaselineScope
    {
        Shared,
        OS
    }

    public sealed class SnapshotCaptureOptions
    {
        public SelectionState? Selection { get; init; }
        public bool HideCursor { get; init; }
        public bool EnableLigatures { get; init; }
        public bool EnableComplexShaping { get; init; } = true;
        /// <summary>
        /// Tri-state primitive overrides. <c>null</c> uses the shipping default, <c>true</c> forces the
        /// primitive path, <c>false</c> forces font-glyph rendering. Explicit <c>false</c> matters now
        /// that primitives are the default — it is the only way to keep the font path under test.
        /// </summary>
        public bool? ForceBoxDrawingPrimitives { get; init; }
        public bool? ForceBlockElementPrimitives { get; init; }
        public double RenderScaling { get; init; } = 1.0;
        public string TypefaceFamily { get; init; } = "Cascadia Code PL, CaskaydiaCove Nerd Font, Cascadia Code, Consolas, Monospace";
        public float FontSize { get; init; } = 14f;

        /// <summary>
        /// Row-picture cache to render with, or <c>null</c> to render uncached.
        /// </summary>
        /// <remarks>
        /// Golden-PNG capture deliberately renders with both caches off, so that every baseline is
        /// produced by the same deterministic path. That also meant nothing in CI ever exercised the
        /// caches — the gap #127 describes. Supplying one here lets a caller render the *same* buffer
        /// twice and assert the second frame hit the cache, which is what detects a regression that
        /// silently invalidates rows every frame.
        ///
        /// Callers own the instance and its disposal; leave it null for baseline captures.
        /// </remarks>
        public RowImageCache? RowCache { get; init; }

        /// <summary>Glyph atlas to render with, or <c>null</c> to render uncached. See <see cref="RowCache"/>.</summary>
        public GlyphCache? GlyphCache { get; init; }
    }

    public static class SnapshotService
    {
        private static readonly object AvaloniaInitGate = new();

        /// <summary>
        /// Captures through the production renderer
        /// (<see cref="TerminalSnapshotRenderer"/>), which is where this method's
        /// body used to live. Baselines therefore pin down the same code the
        /// agent-host <c>captureScreen</c> path and any CLI PNG output run; the
        /// test-only knobs (Avalonia bootstrap, primitive-rendering overrides)
        /// stay here.
        /// </summary>
        public static SKBitmap Capture(TerminalBuffer buffer, CellMetrics metrics, int width, int height, SnapshotCaptureOptions? options = null)
        {
            options ??= new SnapshotCaptureOptions();
            EnsureAvaloniaInitialized();

            IDisposable? primitiveOverride = null;
            if (options.ForceBoxDrawingPrimitives.HasValue || options.ForceBlockElementPrimitives.HasValue)
            {
                primitiveOverride = TerminalDrawOperation.PushPrimitiveRenderingOverrideForTests(
                    useBoxDrawingPrimitives: options.ForceBoxDrawingPrimitives
                        ?? TerminalDrawOperation.DefaultBoxDrawingPrimitivesEnabledForTests,
                    useBlockElementPrimitives: options.ForceBlockElementPrimitives
                        ?? TerminalDrawOperation.DefaultBlockElementPrimitivesEnabledForTests);
            }

            try
            {
                return TerminalSnapshotRenderer.Capture(buffer, metrics, width, height, ToRendererOptions(options));
            }
            finally
            {
                primitiveOverride?.Dispose();
            }
        }

        /// <summary>
        /// Maps the test options onto the renderer's. Everything the baselines
        /// depend on keeps its historical value: no background fill, Skia's plain
        /// family lookup, opacity 1.0, no fallback chain.
        /// </summary>
        private static TerminalSnapshotOptions ToRendererOptions(SnapshotCaptureOptions options) => new()
        {
            Selection = options.Selection,
            HideCursor = options.HideCursor,
            EnableLigatures = options.EnableLigatures,
            EnableComplexShaping = options.EnableComplexShaping,
            RenderScaling = options.RenderScaling,
            TypefaceFamily = options.TypefaceFamily,
            FontSize = options.FontSize,
            RowCache = options.RowCache,
            GlyphCache = options.GlyphCache,
        };

        public static byte[] CapturePng(TerminalBuffer buffer, CellMetrics metrics, int width, int height, SnapshotCaptureOptions? options = null)
        {
            using var bitmap = Capture(buffer, metrics, width, height, options);
            return EncodePng(bitmap);
        }

        public static byte[] EncodePng(SKBitmap bitmap) => TerminalSnapshotRenderer.EncodePng(bitmap);

        public static void CompareToBaseline(BaselineScope scope, string name, byte[] actualPngBytes)
        {
            if (actualPngBytes == null || actualPngBytes.Length == 0)
            {
                throw new XunitException("Actual PNG bytes were empty.");
            }

            bool updateSnapshots = IsEnvFlagEnabled("UPDATE_SNAPSHOTS");
            string normalizedName = NormalizeBaselineName(scope, name);
            string baselinePath = GetBaselinePath(scope, normalizedName);

            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);

            if (updateSnapshots)
            {
                File.WriteAllBytes(baselinePath, actualPngBytes);
                return;
            }

            if (!File.Exists(baselinePath))
            {
                throw new XunitException(
                    $"Baseline is missing: '{baselinePath}'. " +
                    $"Generate it with UPDATE_SNAPSHOTS=1 for this test scope.");
            }

            using var expected = SKBitmap.Decode(baselinePath);
            using var actual = SKBitmap.Decode(actualPngBytes);

            if (expected == null)
            {
                throw new XunitException($"Failed to decode expected baseline PNG: '{baselinePath}'.");
            }

            if (actual == null)
            {
                throw new XunitException("Failed to decode actual rendered PNG bytes.");
            }

            if (StrictPixelEquals(expected, actual))
            {
                return;
            }

            string diffDir = Path.Combine(GetTestOutputRoot(), "Diffs");
            Directory.CreateDirectory(diffDir);

            string cleanName = MakeArtifactSafeName($"{GetScopeFolderName(scope)}_{normalizedName}");
            string expectedPath = Path.Combine(diffDir, $"{cleanName}_expected.png");
            string actualPath = Path.Combine(diffDir, $"{cleanName}_actual.png");
            string diffPath = Path.Combine(diffDir, $"{cleanName}_diff.png");

            File.Copy(baselinePath, expectedPath, overwrite: true);
            File.WriteAllBytes(actualPath, actualPngBytes);

            using (var diffBitmap = GenerateDiff(expected, actual))
            using (var diffImage = SKImage.FromBitmap(diffBitmap))
            using (var diffData = diffImage.Encode(SKEncodedImageFormat.Png, 100))
            using (var diffStream = File.Create(diffPath))
            {
                diffData.SaveTo(diffStream);
            }

            throw new XunitException(
                $"Golden PNG mismatch for '{normalizedName}' ({scope}). " +
                $"Expected: {expected.Width}x{expected.Height}, Actual: {actual.Width}x{actual.Height}. " +
                $"Baseline: {baselinePath}. Diff artifacts: {diffDir}");
        }

        public static void CompareToBaseline(BaselineScope scope, string name, SKBitmap actualBitmap)
            => CompareToBaseline(scope, name, EncodePng(actualBitmap));

        public static void CompareToBaseline(string legacyName, SKBitmap actualBitmap)
            => CompareToBaseline(BaselineScope.Shared, legacyName, actualBitmap);

        private static string GetBaselinePath(BaselineScope scope, string normalizedName)
        {
            string folder = GetBaselineFolder(scope);
            return Path.Combine(folder, $"{normalizedName}.png");
        }

        private static string GetBaselineFolder(BaselineScope scope)
        {
            string baselineRoot = Path.Combine(GetRepoTestRoot(), "Baselines", "Golden");
            if (scope == BaselineScope.Shared)
            {
                return Path.Combine(baselineRoot, "shared");
            }

            return Path.Combine(baselineRoot, GetScopeFolderName(scope));
        }

        private static string GetScopeFolderName(BaselineScope scope)
        {
            if (scope == BaselineScope.Shared)
            {
                return "shared";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "win";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "osx";
            }

            return "unknown";
        }

        private static string NormalizeBaselineName(BaselineScope scope, string name)
        {
            string normalized = name.Replace('\\', '/').Trim('/');
            if (scope == BaselineScope.Shared && normalized.StartsWith("shared/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("shared/".Length);
            }

            return normalized;
        }

        private static bool StrictPixelEquals(SKBitmap expected, SKBitmap actual)
        {
            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                return false;
            }

            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                {
                    if (expected.GetPixel(x, y) != actual.GetPixel(x, y))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static SKBitmap GenerateDiff(SKBitmap expected, SKBitmap actual)
        {
            int width = Math.Max(expected.Width, actual.Width);
            int height = Math.Max(expected.Height, actual.Height);
            var diff = new SKBitmap(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool inExpected = x < expected.Width && y < expected.Height;
                    bool inActual = x < actual.Width && y < actual.Height;

                    if (inExpected && inActual)
                    {
                        SKColor expectedPixel = expected.GetPixel(x, y);
                        SKColor actualPixel = actual.GetPixel(x, y);
                        if (expectedPixel == actualPixel)
                        {
                            diff.SetPixel(x, y, new SKColor(expectedPixel.Red, expectedPixel.Green, expectedPixel.Blue, 64));
                        }
                        else
                        {
                            diff.SetPixel(x, y, new SKColor(255, 0, 0, 255));
                        }
                    }
                    else if (inExpected)
                    {
                        diff.SetPixel(x, y, new SKColor(0, 0, 255, 255));
                    }
                    else if (inActual)
                    {
                        diff.SetPixel(x, y, new SKColor(0, 255, 0, 255));
                    }
                }
            }

            return diff;
        }

        private static bool IsEnvFlagEnabled(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeArtifactSafeName(string name)
            => name.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(' ', '_');

        private static string GetTestOutputRoot()
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestOutput");

        /// <summary>
        /// Boots enough of Avalonia for font resolution. Public because callers
        /// that use <see cref="TerminalSnapshotRenderer"/> directly (rather than
        /// through <see cref="Capture"/>) still need the font manager up.
        /// </summary>
        public static void EnsureAvaloniaInitialized()
        {
            if (Application.Current != null)
            {
                return;
            }

            lock (AvaloniaInitGate)
            {
                if (Application.Current != null)
                {
                    return;
                }

                NovaTerminal.Tests.TestAppBuilder.BuildAvaloniaApp().SetupWithoutStarting();
            }
        }

        // Use a heuristic to find the repo root so we write baselines into the source tree.
        private static string GetRepoTestRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "tests", "NovaTerminal.App.Tests")))
                {
                    return Path.Combine(dir.FullName, "tests", "NovaTerminal.App.Tests");
                }

                dir = dir.Parent;
            }

            // Fallback to current test output root when repo discovery fails.
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
