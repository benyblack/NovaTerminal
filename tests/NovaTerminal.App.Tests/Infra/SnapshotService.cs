using NovaTerminal.Shell;
using Avalonia;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using SkiaSharp;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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
        /// Makes sure the Avalonia headless platform is up, so font resolution works — without
        /// ever booting it on the calling thread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to call <c>SetupWithoutStarting</c> on a <c>TestAppBuilder.BuildAvaloniaApp()</c>
        /// builder directly (spelled without the call parentheses here so the
        /// <c>NothingInTheTestSuiteBootsAvaloniaDirectly</c> source scan does not flag this prose).
        /// Callers reach it from plain <c>[Fact]</c> bodies and constructors, so that
        /// booted the platform on whichever thread xUnit happened to pick, which is
        /// fundamentally incompatible with <c>Avalonia.Headless.XUnit</c> driving the same global
        /// state from its own single dispatch thread. The collision has four faces, all
        /// order-dependent, all invisible when the affected class runs alone:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <c>AvaloniaHeadlessPlatform.Initialize</c> constructs a <c>Compositor</c>, which reads
        /// <c>MediaContext.Instance</c>. That lazily binds a <em>thread-affine</em>
        /// <c>MediaContext</c> into <c>AvaloniaLocator.CurrentMutable</c> — the process-global
        /// ROOT, because a plain <c>[Fact]</c> runs outside the per-test
        /// <c>AvaloniaLocator.EnterScope()</c>. Locator scopes fall through to their parent on a
        /// miss, so every later <c>[AvaloniaFact]</c> inherits a context owned by that worker
        /// thread and never binds its own. <c>Dispatcher.CheckAccess()</c> is a bare
        /// <c>Thread.CurrentThread == _thread</c>, so the first transition or animation any of
        /// them applies throws "The calling thread cannot access this object because a different
        /// thread owns it" — 13 CI failures across four unrelated classes, plus three downstream
        /// layout assertions.
        /// </description></item>
        /// <item><description>
        /// Run the other way round, with an <c>[AvaloniaFact]</c> first, and
        /// <c>SetupWithoutStarting</c> hits Avalonia's process-wide setup guard instead:
        /// "Setup was already called on one of AppBuilder instances". The session itself uses the
        /// unguarded <c>SetupUnsafe</c>, so only this call site tripped it.
        /// </description></item>
        /// <item><description>
        /// If a session teardown left <c>Dispatcher.UIThread</c> owned by the session thread, the
        /// boot threw cross-thread partway through, on this very call.
        /// </description></item>
        /// <item><description>
        /// Mixed states hang: a <c>MediaContext</c> and a <c>Compositor</c> owned by different
        /// threads leave render waits that never complete.
        /// </description></item>
        /// </list>
        /// <para>
        /// So the platform is now booted only ever by <c>HeadlessUnitTestSession</c>, on its
        /// dispatch thread, by dispatching a no-op through it. Under
        /// <c>AvaloniaTestIsolationLevel.PerAssembly</c> (set in <c>TestAppBuilder</c>) the
        /// session sets the application up once and never tears it down, so
        /// <c>Application.Current</c> and the font manager stay available afterwards — including
        /// to plain <c>[Fact]</c> callers on their own threads, since Avalonia's service locator
        /// is process-global. Nothing these callers do needs the dispatcher thread: the snapshot
        /// renderer draws straight into a Skia surface.
        /// </para>
        /// <para>
        /// Note that serializing the callers into one non-parallel collection (the #317
        /// mitigation in <c>AvaloniaTestSchedulingTests</c>) never addressed any of this. The
        /// damage is lasting global state, not a race, so only test order ever mattered.
        /// </para>
        /// </remarks>
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

                // Deliberately no `if (Dispatcher.UIThread.CheckAccess()) return;` guard here.
                // It reads like the right way to avoid dispatching from inside a dispatch (which
                // would queue behind the operation we are in and deadlock), but
                // Dispatcher.UIThread creates its instance on first touch and keeps that thread
                // (`s_uiThread ??= this`), so a plain [Fact] that gets here first makes
                // CheckAccess() true on its own worker thread - and the guard would then skip
                // initialization altogether, leaving the font manager down.
                //
                // The `Application.Current != null` checks above already cover the re-entrant
                // case: the session sets the application up before invoking any test body, so
                // anything running on the dispatch thread has already returned by now.
                AssemblyHeadlessSessionWarmup.Ensure();
            }
        }

        /// <summary>
        /// The thread that owns the ambient <c>MediaContext</c>, or <c>null</c> when none is
        /// bound. Test seam for <c>AvaloniaBootLocatorHygieneTests</c>: called from a plain
        /// <c>[Fact]</c> this must never be the calling thread, because that would mean the
        /// headless platform got booted off the session's dispatch thread again — see
        /// <see cref="EnsureAvaloniaInitialized"/> for what that breaks.
        /// </summary>
        /// <remarks>
        /// Reflection throughout, because <c>AvaloniaLocator.Current</c> and <c>MediaContext</c>
        /// are internal in the shipped Avalonia assemblies. It throws rather than returning
        /// <c>null</c> when a member is missing: a seam that silently reports "nothing bound"
        /// after an Avalonia upgrade would leave the guard permanently, invisibly green.
        /// </remarks>
        internal static Thread? AmbientMediaContextOwnerThreadForTest()
        {
            const BindingFlags StaticMembers =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;

            var locatorType = typeof(AvaloniaLocator);
            var current = locatorType.GetProperty("Current", StaticMembers)?.GetValue(null)
                ?? throw new InvalidOperationException(
                    "AvaloniaLocator.Current is gone; re-port AmbientMediaContextOwnerThreadForTest.");

            var mediaContextType = typeof(AvaloniaObject).Assembly.GetType("Avalonia.Media.MediaContext")
                ?? throw new InvalidOperationException(
                    "Avalonia.Media.MediaContext is gone; re-port AmbientMediaContextOwnerThreadForTest.");

            var getService = current.GetType().GetMethod("GetService", new[] { typeof(Type) })
                ?? throw new InvalidOperationException(
                    "IAvaloniaDependencyResolver.GetService is gone; re-port AmbientMediaContextOwnerThreadForTest.");

            var mediaContext = getService.Invoke(current, new object[] { mediaContextType });
            if (mediaContext is null)
            {
                return null;
            }

            var dispatcherField = mediaContextType.GetField("_dispatcher", InstanceMembers)
                ?? throw new InvalidOperationException(
                    "MediaContext._dispatcher is gone; re-port AmbientMediaContextOwnerThreadForTest.");
            var dispatcher = dispatcherField.GetValue(mediaContext)
                ?? throw new InvalidOperationException("MediaContext._dispatcher was null.");

            var threadField = dispatcher.GetType().GetField("_thread", InstanceMembers)
                ?? throw new InvalidOperationException(
                    "Dispatcher._thread is gone; re-port AmbientMediaContextOwnerThreadForTest.");

            return threadField.GetValue(dispatcher) as Thread;
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
