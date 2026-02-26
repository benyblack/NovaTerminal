using System;
using System.IO;
using System.Text.Json;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    public sealed class RenderPerfWriterTests
    {
        [Fact]
        public void CreateFromEnvironment_DisabledFlag_ReturnsNull()
        {
            string? previousEnabled = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS");
            string? previousOut = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT");

            try
            {
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", null);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", null);

                using RenderPerfWriter? writer = RenderPerfWriter.CreateFromEnvironment();
                Assert.Null(writer);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", previousEnabled);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", previousOut);
            }
        }

        [Fact]
        public void CreateFromEnvironment_Enabled_WritesCompactJsonl()
        {
            string? previousEnabled = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS");
            string? previousOut = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT");
            string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-renderperf-tests", Guid.NewGuid().ToString("N"));
            string outPath = Path.Combine(tempDir, "render_metrics.jsonl");

            try
            {
                Directory.CreateDirectory(tempDir);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", "1");
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", outPath);

                using RenderPerfWriter? writer = RenderPerfWriter.CreateFromEnvironment();
                Assert.NotNull(writer);

                var metrics = new RenderPerfMetrics
                {
                    FrameIndex = 7,
                    FrameTimeMs = 2.5,
                    DirtyRows = 3,
                    DirtySpansTotal = 3,
                    DrawCallsText = 2,
                    DrawCallsRects = 1,
                    DrawCallsTotal = 3,
                    RowPictureCacheHits = 4,
                    RowPictureCacheMisses = 1,
                    PictureBuilds = 1,
                    FlushCount = 2,
                    AtlasAlphaGlyphs = 5,
                    AtlasColorGlyphs = 6,
                    DirectDrawTextCount = 2,
                    ShapedTextRuns = 1,
                    AllocBytesThisFrame = 128
                };

                writer!.TryWrite(metrics);
                writer.Dispose();

                Assert.True(File.Exists(outPath));
                string[] lines = File.ReadAllLines(outPath);
                Assert.Single(lines);

                using JsonDocument doc = JsonDocument.Parse(lines[0]);
                Assert.Equal(7, doc.RootElement.GetProperty("FrameIndex").GetInt64());
                Assert.Equal(3, doc.RootElement.GetProperty("DrawCallsTotal").GetInt32());
                Assert.Equal(128, doc.RootElement.GetProperty("AllocBytesThisFrame").GetInt64());
            }
            finally
            {
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", previousEnabled);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", previousOut);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void CreateFromEnvironment_InvalidOutput_DoesNotThrow()
        {
            string? previousEnabled = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS");
            string? previousOut = Environment.GetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT");
            string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-renderperf-tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(tempDir);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", "1");
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", tempDir);

                using RenderPerfWriter? writer = RenderPerfWriter.CreateFromEnvironment();
                Assert.Null(writer);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS", previousEnabled);
                Environment.SetEnvironmentVariable("NOVATERM_RENDER_METRICS_OUT", previousOut);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void TryWrite_BuffersUntilFlushThresholdOrDispose()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "novaterm-renderperf-tests", Guid.NewGuid().ToString("N"));
            string outPath = Path.Combine(tempDir, "render_metrics_buffered.jsonl");

            try
            {
                Directory.CreateDirectory(tempDir);
                using RenderPerfWriter? writer = RenderPerfWriter.Create(outPath);
                Assert.NotNull(writer);

                writer!.TryWrite(new RenderPerfMetrics { FrameIndex = 1, FrameTimeMs = 1.0 });
                Assert.True(File.Exists(outPath));

                long lenAfterFirstWrite = new FileInfo(outPath).Length;
                Assert.Equal(0, lenAfterFirstWrite);

                for (int i = 2; i <= 60; i++)
                {
                    writer.TryWrite(new RenderPerfMetrics { FrameIndex = i, FrameTimeMs = 1.0 });
                }

                long lenAfterThreshold = new FileInfo(outPath).Length;
                Assert.True(lenAfterThreshold > 0);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
    }
}
