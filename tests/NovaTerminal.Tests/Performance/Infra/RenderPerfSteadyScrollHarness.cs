using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media;
using NovaTerminal.Core;
using SkiaSharp;
using Xunit.Sdk;

namespace NovaTerminal.Tests.Performance.Infra
{
    internal sealed class RenderPerfRunResult : IDisposable
    {
        public string TempDir { get; }
        public string OutputPath { get; }
        public int WarmupFrames { get; }
        public int MeasuredFrames { get; }
        public int TotalFramesRendered { get; }
        public IReadOnlyList<RenderPerfMetrics> Frames { get; }

        public RenderPerfRunResult(
            string tempDir,
            string outputPath,
            int warmupFrames,
            int measuredFrames,
            int totalFramesRendered,
            IReadOnlyList<RenderPerfMetrics> frames)
        {
            TempDir = tempDir;
            OutputPath = outputPath;
            WarmupFrames = warmupFrames;
            MeasuredFrames = measuredFrames;
            TotalFramesRendered = totalFramesRendered;
            Frames = frames;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(TempDir))
                {
                    Directory.Delete(TempDir, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should be best-effort.
            }
        }
    }

    internal static class RenderPerfSteadyScrollHarness
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        public static RenderPerfRunResult Run(int warmupFrames, int measuredFrames)
        {
            if (warmupFrames <= 0 || measuredFrames <= 0)
            {
                throw new XunitException($"Warmup and measured frames must be > 0 (warmup={warmupFrames}, measured={measuredFrames}).");
            }

            const int cols = 20;
            const int rows = 6;
            int totalFrames = warmupFrames + measuredFrames;

            string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-perf", Guid.NewGuid().ToString("N"));
            string outPath = Path.Combine(tempDir, "render_metrics.jsonl");
            Directory.CreateDirectory(tempDir);

            string? previousEnabled = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS");
            string? previousOut = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT");

            try
            {
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", "1");
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", outPath);
                TerminalDrawOperation.ResetRenderPerfWriterForTests();

                int width = (int)Math.Ceiling((cols * Metrics.CellWidth) + 8);
                int height = (int)Math.Ceiling(rows * Metrics.CellHeight);

                using var bitmap = new SKBitmap(width, height);
                using var canvas = new SKCanvas(bitmap);
                using var rowCache = new RowImageCache();

                var buffer = new TerminalBuffer(cols, rows);
                var parser = new AnsiParser(buffer);

                for (int i = 0; i < rows; i++)
                {
                    parser.Process(BuildFrameLine(i));
                }

                var typeface = new Typeface("Consolas, Monospace");
                var glyphTypeface = typeface.GlyphTypeface;
                var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
                var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));

                try
                {
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
                        opacity: 1.0,
                        transparentBackground: false,
                        hideCursor: true,
                        renderScaling: 1.0,
                        snapshotRows: buffer.Rows,
                        snapshotCols: buffer.Cols,
                        totalLines: buffer.TotalLines,
                        cursorRow: buffer.CursorRow,
                        cursorCol: buffer.CursorCol,
                        rowCache: rowCache,
                        enableComplexShaping: false,
                        glyphCache: null);

                    try
                    {
                        for (int frame = 0; frame < totalFrames; frame++)
                        {
                            parser.Process(BuildFrameLine(frame + rows));
                            op.DrawTerminalInternal(canvas);
                        }
                    }
                    finally
                    {
                        op.Dispose();
                    }
                }
                finally
                {
                    skFont.Dispose();
                    skTypeface.Dispose();
                }

                // Dispose/flush current writer so all buffered lines are visible to the parser.
                TerminalDrawOperation.ResetRenderPerfWriterForTests();
                List<RenderPerfMetrics> frames = RenderPerfJsonl.ReadAllFrames(outPath);

                return new RenderPerfRunResult(
                    tempDir,
                    outPath,
                    warmupFrames,
                    measuredFrames,
                    totalFrames,
                    frames);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", previousEnabled);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", previousOut);
                TerminalDrawOperation.ResetRenderPerfWriterForTests();
            }
        }

        private static string BuildFrameLine(int frameIndex)
            => $"steady-scroll frame={frameIndex:D5} payload=ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n";
    }
}
