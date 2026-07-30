using System;
using NovaTerminal.Shell;
using Avalonia.Headless.XUnit;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// #127: the rendering pipeline's caches were not protected by CI. A regression that silently
    /// defeated the row cache — say an accidental per-frame invalidation — would have passed cleanly.
    ///
    /// The `Render Metrics` CI job already existed and looked like protection, but the tests behind it
    /// only called <c>RendererStatistics.RecordFrame(...)</c> by hand and asserted the counter moved.
    /// They never rendered anything. `RendererMetricsTests` says as much in a closing comment. A job
    /// that tests the bookkeeping class rather than the renderer is worse than no job: it reads as
    /// coverage.
    ///
    /// These tests render for real, through <c>TerminalDrawOperation</c> to an offscreen surface, and
    /// assert on counters rather than on frame *times* — deterministic on a shared CI runner, where a
    /// millisecond threshold would flake. #127 defers timing thresholds to nightly for that reason;
    /// this is the part that can gate a PR today.
    ///
    /// Tagged `RenderMetrics` so the existing job picks them up with no workflow change.
    /// </summary>
    [Trait("Category", "RenderMetrics")]
    [Collection("RendererStatistics")]
    public sealed class RenderCacheEffectivenessTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        private const int Cols = 40;
        private const int Rows = 10;

        private static TerminalBuffer BufferWithContent()
        {
            var buffer = new TerminalBuffer(Cols, Rows);
            var parser = new AnsiParser(buffer);
            for (int i = 0; i < Rows - 1; i++)
            {
                parser.Process($"line {i} with some content\r\n");
            }
            return buffer;
        }

        private static void Render(TerminalBuffer buffer, RowImageCache? rowCache, GlyphCache? glyphCache)
        {
            using var bitmap = SnapshotService.Capture(
                buffer,
                Metrics,
                (int)(Cols * Metrics.CellWidth),
                (int)(Rows * Metrics.CellHeight),
                new SnapshotCaptureOptions
                {
                    HideCursor = true,
                    RowCache = rowCache,
                    GlyphCache = glyphCache
                });
        }

        [AvaloniaFact]
        public void SecondFrameOfUnchangedContent_IsServedFromTheRowCache()
        {
            var buffer = BufferWithContent();
            using var rowCache = new RowImageCache();

            // Frame 1 populates the cache: every renderable row is a miss.
            RendererStatistics.Reset();
            Render(buffer, rowCache, glyphCache: null);
            long firstHits = RendererStatistics.RowCacheHits;
            long firstMisses = RendererStatistics.RowCacheMisses;

            Assert.True(firstMisses > 0, "first frame should have populated the row cache");
            Assert.Equal(0, firstHits);

            // Frame 2 renders the same unchanged buffer. Row identity and revision are unchanged, so
            // every row that was cached must come back as a hit. This is the assertion that fails if
            // something starts invalidating rows every frame.
            RendererStatistics.Reset();
            Render(buffer, rowCache, glyphCache: null);
            long secondHits = RendererStatistics.RowCacheHits;
            long secondMisses = RendererStatistics.RowCacheMisses;

            Assert.True(
                secondHits > 0,
                $"second frame of identical content produced {secondHits} row-cache hits and "
                + $"{secondMisses} misses - the row cache is not being consulted or is being "
                + "invalidated every frame (#127).");

            Assert.True(
                secondHits >= secondMisses,
                $"second frame of identical content was mostly misses ({secondMisses} misses vs "
                + $"{secondHits} hits) - row caching has regressed (#127).");
        }

        [AvaloniaFact]
        public void MutatingOneRow_InvalidatesOnlyThatRow()
        {
            // The complement of the test above, and the reason it isn't enough on its own: a cache
            // that never invalidates would also pass "second frame is all hits". Touching one row must
            // cost exactly one miss, not a full redraw.
            var buffer = BufferWithContent();
            using var rowCache = new RowImageCache();

            Render(buffer, rowCache, glyphCache: null);   // populate

            // Rewrite the top row in place. Its revision changes; every other row's does not.
            var parser = new AnsiParser(buffer);
            parser.Process("\u001b[1;1H");
            parser.Process("CHANGED");

            RendererStatistics.Reset();
            Render(buffer, rowCache, glyphCache: null);

            long hits = RendererStatistics.RowCacheHits;
            long misses = RendererStatistics.RowCacheMisses;

            Assert.True(hits > 0, $"expected untouched rows to stay cached; got {hits} hits");

            // Both bounds are load-bearing, and the lower one is easy to forget: without it a cache
            // that ignored the revision entirely - always hitting, never noticing the edit - would
            // pass this test while rendering stale pixels. Verified by mutating Key.Equals to compare
            // only RowId, which makes this assertion the one that fires.
            Assert.True(
                misses >= 1,
                $"editing a row produced {misses} row-cache misses - the edited row was served from "
                + "cache, so the revision is not part of the key and the frame renders stale (#127).");

            Assert.True(
                misses <= 2,
                $"editing one row caused {misses} row-cache misses (with {hits} hits) - the "
                + "invalidation is wider than the change (#127). Two are allowed: the edited row, and "
                + "the cursor row if it differs.");
        }

        [AvaloniaFact]
        public void GlyphAtlasIsReusedAcrossFrames_WithoutResetting()
        {
            var buffer = BufferWithContent();
            using var glyphCache = new GlyphCache();

            long resetsBefore = RendererStatistics.GlyphAtlasResets;

            Render(buffer, rowCache: null, glyphCache: glyphCache);
            int entriesAfterFirstFrame = glyphCache.EntryCount;
            Assert.True(entriesAfterFirstFrame > 0, "first frame should have populated the glyph atlas");

            // Same content again: the atlas should be reused, not rebuilt. Entry count must not grow,
            // because every glyph is already resident, and the atlas must not have been reset.
            for (int i = 0; i < 5; i++)
            {
                Render(buffer, rowCache: null, glyphCache: glyphCache);
            }

            Assert.Equal(entriesAfterFirstFrame, glyphCache.EntryCount);
            Assert.Equal(resetsBefore, RendererStatistics.GlyphAtlasResets);
        }

        [AvaloniaFact]
        public void RenderingWithoutARowCache_ReportsNoHits()
        {
            // Guards the guard: if `RowCache = null` still produced hits, the tests above would be
            // measuring something other than the cache they think they are.
            var buffer = BufferWithContent();

            RendererStatistics.Reset();
            Render(buffer, rowCache: null, glyphCache: null);
            Render(buffer, rowCache: null, glyphCache: null);

            Assert.Equal(0, RendererStatistics.RowCacheHits);
            Assert.True(RendererStatistics.RowCacheMisses > 0);
        }
    }
}
