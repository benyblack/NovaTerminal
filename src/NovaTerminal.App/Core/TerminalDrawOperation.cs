using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace NovaTerminal.Core
{
    public sealed class TerminalDrawOperation : ICustomDrawOperation
    {
        private readonly TerminalBuffer _buffer;
        private readonly CellMetrics _metrics;
        private readonly int _scrollOffset;
        private readonly SelectionState _selection;
        private readonly List<SearchMatch>? _searchMatches;
        private readonly int _activeSearchIndex;
        private readonly Typeface _typeface;
        private readonly double _fontSize;
        private readonly Rect _bounds;
        private readonly IGlyphTypeface _glyphTypeface;
        private readonly SharedSKTypeface? _skTypeface;
        private readonly SharedSKFont? _skFont;
        private readonly bool _enableLigatures;
        private readonly ConcurrentDictionary<string, SKTypeface?> _fallbackCache;
        private readonly SKTypeface[] _fallbackChain;
        private readonly float _opacity;
        private readonly bool _transparentBackground;
        private readonly bool _hideCursor;
        private readonly double _renderScaling;
        private readonly int _bufferRows;
        private readonly int _bufferCols;
        private readonly RowImageCache? _rowCache;
        private readonly bool _enableComplexShaping;
        private readonly GlyphCache? _glyphCache;
        private readonly int _cellWidthDevicePx;
        private readonly int _cellHeightDevicePx;
        private readonly PixelGrid _pixelGrid;
        private readonly bool _showRenderHud;
        private bool _wasAltScreenLastFrame;

        private static readonly bool GlyphDiagnosticsEnabled = IsEnvFlagEnabled("NOVATERM_DIAG_GLYPH");
        private static readonly bool GridDiagnosticsEnabled = IsEnvFlagEnabled("NOVATERM_DIAG_GRID");
        private static readonly bool ForceKnownGoodBoxFont = IsEnvFlagEnabled("NOVATERM_FORCE_BOX_FONT");
        private static readonly bool UseBoxDrawingPrimitives = IsEnvFlagEnabled("NOVATERM_BOX_PRIMITIVES");
        private static readonly bool UseBlockElementPrimitives = IsEnvFlagEnabled("NOVATERM_BLOCK_PRIMITIVES");
        private static readonly AsyncLocal<TestPrimitiveRenderOverride?> PrimitiveRenderOverrideForTests = new();
        private static readonly ConcurrentDictionary<string, byte> GlyphDiagOnce = new();
        private static readonly string[] KnownGoodBoxFonts = { "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", "Cascadia Code" };
        private static Lazy<RenderPerfWriter?> SharedRenderPerfWriter = new(RenderPerfWriter.CreateFromEnvironment);

        // Batching for DrawAtlas
        private const int InitialAtlasBatchCapacity = 128;
        private SKRect[] _alphaRects = new SKRect[InitialAtlasBatchCapacity];
        private SKRotationScaleMatrix[] _alphaXforms = new SKRotationScaleMatrix[InitialAtlasBatchCapacity];
        private SKColor[] _alphaColors = new SKColor[InitialAtlasBatchCapacity];
        private SKRect[] _colorRects = new SKRect[InitialAtlasBatchCapacity];
        private SKRotationScaleMatrix[] _colorXforms = new SKRotationScaleMatrix[InitialAtlasBatchCapacity];
        private SKRect[]? _alphaRectsScratch;
        private SKRotationScaleMatrix[]? _alphaXformsScratch;
        private SKColor[]? _alphaColorsScratch;
        private SKRect[]? _colorRectsScratch;
        private SKRotationScaleMatrix[]? _colorXformsScratch;
        private int _alphaCount;
        private int _alphaScratchPrevCount;
        private int _colorCount;
        private int _colorScratchPrevCount;
        private readonly SKPaint _bgFillPaint = new();
        private readonly SKPaint _decoStrokePaint = new();
        private readonly SKPaint _blockFillPaint = new();
        private readonly SKPaint _shadeFillPaint = new();
        private readonly SKPaint _atlasAlphaPaint = new();
        private readonly SKPaint _atlasColorPaint = new();
        private readonly Dictionary<SKTypeface, SKFont> _fallbackFontCache = new(SKTypefaceReferenceComparer.Instance);
        private readonly Dictionary<SKTypeface, ShapingResources> _shapingCache = new(SKTypefaceReferenceComparer.Instance);
        private const int RunBuilderInitialCapacity = 256;
        private const int RunBuilderMaxCapacity = 4096;
        private readonly StringBuilder _runBuilder = new(capacity: RunBuilderInitialCapacity);
        private float[]? _colEdges;
        private float[]? _rowEdges;
        private int _colEdgesCount;
        private int _rowEdgesCount;
        private RenderPerfMetrics _framePerfMetrics;
        private long _frameAllocStartBytes;
        private int _frameOtherDrawCalls;
        private bool _collectFramePerfMetrics;

        private struct RowRenderItem
        {
            public int AbsRow;
            public SKPicture? CachedPicture;
            public RenderRowSnapshot? Snapshot;
        }

        private class FrameSnapshot
        {
            public SKColor ThemeBg;
            public SKColor ThemeFg;
            public SKColor CursorColor;
            public CursorStyle CursorStyle;
            public List<RowRenderItem> RowItems = new();
            public List<RenderImageSnapshot> Images = new();
        }

        private sealed class ShapingResources
        {
            public SKShaper Shaper { get; }
            public SKFont Font { get; }

            public ShapingResources(SKShaper shaper, SKFont font)
            {
                Shaper = shaper;
                Font = font;
            }
        }

        private sealed class TestPrimitiveRenderOverride
        {
            public bool UseBoxDrawingPrimitives { get; init; }
            public bool UseBlockElementPrimitives { get; init; }
        }

        private sealed class PrimitiveRenderOverrideScope : IDisposable
        {
            private readonly TestPrimitiveRenderOverride? _previous;
            private bool _disposed;

            public PrimitiveRenderOverrideScope(TestPrimitiveRenderOverride? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                PrimitiveRenderOverrideForTests.Value = _previous;
            }
        }

        public Rect Bounds => _bounds;

        public TerminalDrawOperation(
            Rect bounds,
            TerminalBuffer buffer,
            int scrollOffset,
            SelectionState selection,
            List<SearchMatch>? searchMatches,
            int activeSearchIndex,
            CellMetrics metrics,
            Typeface typeface,
            double fontSize,
            IGlyphTypeface glyphTypeface,
            SharedSKTypeface? skTypeface,
            SharedSKFont? skFont,
            bool enableLigatures,
            ConcurrentDictionary<string, SKTypeface?> fallbackCache,
            SKTypeface[] fallbackChain,
            double opacity,
            bool transparentBackground,
            bool hideCursor,
            double renderScaling = 1.0,
            int snapshotRows = 0,
            int snapshotCols = 0,
            int totalLines = 0,
            int cursorRow = 0,
            int cursorCol = 0,
            RowImageCache? rowCache = null,
            bool enableComplexShaping = true,
            GlyphCache? glyphCache = null,
            bool showRenderHud = false)
        {
            _bounds = bounds;
            _buffer = buffer;
            _scrollOffset = scrollOffset;
            _selection = selection;
            _searchMatches = searchMatches;
            _activeSearchIndex = activeSearchIndex;
            _metrics = metrics;
            _typeface = typeface;
            _fontSize = fontSize;
            _glyphTypeface = glyphTypeface;
            _skTypeface = skTypeface;
            _skTypeface?.Increment();
            _skFont = skFont;
            _skFont?.Increment();
            _enableLigatures = enableLigatures;
            _fallbackCache = fallbackCache;
            _fallbackChain = fallbackChain;
            _opacity = (float)Math.Clamp(opacity, 0.0, 1.0);
            _transparentBackground = transparentBackground;
            _hideCursor = hideCursor;
            _renderScaling = renderScaling;
            _bufferRows = snapshotRows;
            _bufferCols = snapshotCols;
            _rowCache = rowCache;
            _enableComplexShaping = enableComplexShaping;
            _glyphCache = glyphCache;
            _showRenderHud = showRenderHud;
            _cellWidthDevicePx = Math.Max(1, ToDevicePx(_metrics.CellWidth));
            _cellHeightDevicePx = Math.Max(1, ToDevicePx(_metrics.CellHeight));
            int baselineOffsetPx = ToDevicePx(_metrics.Baseline);
            int underlineOffsetPx = ToDevicePx(_metrics.CellHeight - Math.Max(1.5f, _metrics.CellHeight * 0.12f));
            int strikeOffsetPx = (int)Math.Round(_cellHeightDevicePx * 0.52, MidpointRounding.AwayFromZero);
            _pixelGrid = new PixelGrid(
                originXPx: ToDevicePx(4f),
                originYPx: 0,
                cellWidthPx: _cellWidthDevicePx,
                cellHeightPx: _cellHeightDevicePx,
                baselineOffsetPx: baselineOffsetPx,
                underlineOffsetPx: underlineOffsetPx,
                strikeOffsetPx: strikeOffsetPx);
        }

        public void Dispose()
        {
            _skFont?.Dispose();
            _skTypeface?.Dispose();

            _alphaCount = 0;
            _alphaScratchPrevCount = 0;
            _colorCount = 0;
            _colorScratchPrevCount = 0;
            ReturnEdgeBuffers();
            _bgFillPaint.Dispose();
            _decoStrokePaint.Dispose();
            _blockFillPaint.Dispose();
            _shadeFillPaint.Dispose();
            _atlasAlphaPaint.Dispose();
            _atlasColorPaint.Dispose();
            DisposeAndClearShapingCache();
            DisposeAndClearFallbackFontCache();
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            using var snapshotToDispose = DrawTerminalInternal(canvas);
            canvas.Restore();
        }

        // Step 3 optimization: rely on row pictures (or DrawRowText fallback) to paint non-default backgrounds.
        private const bool UseRowPicturesForBackgrounds = true;

        // Step 5: Always build SKPictures for dirty rows — skipping caching causes stale
        // cache hits on subsequent frames (mass invalidation "ghost" bug).
        private const int MaxPictureBuildsPerFrame = int.MaxValue;
        private const float MassInvalidationThreshold = 0.5f; // kept for stats only
        private const int SpanRenderMaxSpansPerRow = 6;
        private const float SpanRenderCoverageFallbackThreshold = 0.70f;

        // Drop-in replacement for TerminalDrawOperation.DrawTerminal(SKCanvas canvas)
        // Fixes:
        // 1) DrainDisposalsAndApplyClearIfRequested() is called OUTSIDE _buffer lock
        // 2) Preserves row->Y mapping even if some rows are missing (uses VisualRow)
        // 3) Uses a single snapshotted absDisplayStart for consistent image/overlay/cursor mapping

        internal TerminalRenderSnapshot? DrawTerminalInternal(SKCanvas canvas)
        {
            try
            {
                // Render-thread safe boundary: drain deferred disposals & apply clear requests.
                _rowCache?.DrainDisposalsAndApplyClearIfRequested();
                _glyphCache?.DrainDisposals();

                RenderPerfWriter? perfWriter = BeginFramePerfMetrics();
                var frame = new FrameSnapshot();
                var snapshotRequest = new RenderSnapshotRequest
                {
                    ViewportRows = _bufferRows,
                    ViewportCols = _bufferCols,
                    ScrollOffset = _scrollOffset,
                    Selection = _selection,
                    SearchMatches = _searchMatches,
                    ActiveSearchIndex = _activeSearchIndex
                };
                TerminalRenderSnapshot renderSnapshot = _buffer.CaptureRenderSnapshot(snapshotRequest, out long readLockMs);
                RendererStatistics.RecordBufferReadLockTimeMs(readLockMs);

                int bufferRows = renderSnapshot.ViewportRows;
                int bufferCols = renderSnapshot.ViewportCols;
                int absDisplayStart = renderSnapshot.AbsDisplayStart;
                byte alpha = (byte)(255 * _opacity);
                int dirtySpanCount = renderSnapshot.DirtySpans.Length;
                var spansByRow = new List<DirtySpan>[bufferRows];
                for (int i = 0; i < dirtySpanCount; i++)
                {
                    var span = renderSnapshot.DirtySpans.Array[i];
                    if ((uint)span.Row >= (uint)bufferRows)
                    {
                        continue;
                    }

                    var bucket = spansByRow[span.Row];
                    if (bucket == null)
                    {
                        bucket = new List<DirtySpan>(4);
                        spansByRow[span.Row] = bucket;
                    }

                    bucket.Add(span);
                }

                int dirtyRowCount = 0;
                for (int i = 0; i < spansByRow.Length; i++)
                {
                    if (spansByRow[i] != null && spansByRow[i]!.Count > 0)
                    {
                        dirtyRowCount++;
                    }
                }
                var frameSw = System.Diagnostics.Stopwatch.StartNew();
                frame.ThemeBg = new SKColor(renderSnapshot.Theme.Background.R, renderSnapshot.Theme.Background.G, renderSnapshot.Theme.Background.B, alpha);
                frame.ThemeFg = new SKColor(renderSnapshot.Theme.Foreground.R, renderSnapshot.Theme.Foreground.G, renderSnapshot.Theme.Foreground.B, 255);
                frame.CursorColor = new SKColor(renderSnapshot.Theme.CursorColor.R, renderSnapshot.Theme.CursorColor.G, renderSnapshot.Theme.CursorColor.B, 255);
                frame.CursorStyle = renderSnapshot.CursorStyle;
                
                if (renderSnapshot.Images.Length > 0 && renderSnapshot.Images.Array != null)
                {
                    frame.Images = new List<RenderImageSnapshot>(renderSnapshot.Images.Length);
                    for (int i = 0; i < renderSnapshot.Images.Length; i++)
                    {
                        frame.Images.Add(renderSnapshot.Images.Array[i]);
                    }
                }
                else
                {
                    frame.Images = new List<RenderImageSnapshot>();
                }

                 // IMPORTANT: Always add exactly one RowRenderItem per visual row
                // so render loop can safely use r for Y positioning.
                // Skip row picture cache for AltScreen (mc, vim, htop etc.) — these apps
                // redraw every row on every focus/resize, giving near-zero cache hit rate.
                // Caching only wastes native Skia memory (~1-5MB per SKPicture × 90 entries).
                bool isAltScreen = _buffer.IsAltScreenActive;
                bool useRowCache = _rowCache != null && !isAltScreen;
                if (!useRowCache && _rowCache != null && !_wasAltScreenLastFrame)
                {
                    // First AltScreen frame: clear any pictures cached from the main-screen session.
                    _rowCache.RequestClear();
                }
                _wasAltScreenLastFrame = isAltScreen;

                for (int r = 0; r < bufferRows; r++)
                {
                    RenderRowSnapshot rowSnapshot = r < renderSnapshot.RowsData.Length && renderSnapshot.RowsData.Array != null
                        ? renderSnapshot.RowsData.Array[r]
                        : new RenderRowSnapshot
                        {
                            AbsRow = absDisplayStart + r,
                            Cols = 0,
                            Cells = Array.Empty<RenderCellSnapshot>(),
                            Revision = 0,
                            RowId = 0
                        };

                    var item = new RowRenderItem
                    {
                        AbsRow = rowSnapshot.AbsRow,
                        CachedPicture = null,
                        Snapshot = null
                    };

                    bool hasRenderableRow = rowSnapshot.Cols > 0 && rowSnapshot.Cells.Length >= rowSnapshot.Cols && rowSnapshot.RowId != 0;
                    if (hasRenderableRow)
                    {
                        item.CachedPicture = useRowCache ? _rowCache?.Get(rowSnapshot.RowId, rowSnapshot.Revision) : null;

                        if (item.CachedPicture != null)
                        {
                            RendererStatistics.RecordRowCacheHit();
                            IncrementRowPictureCacheHit();
                        }
                        else
                        {
                            RendererStatistics.RecordRowCacheMiss();
                            IncrementRowPictureCacheMiss();
                            item.Snapshot = rowSnapshot;
                            RendererStatistics.RecordRowSnapshot();
                        }
                    }

                    frame.RowItems.Add(item);
                }


                // --------------------
                // Render Phase (LOCK RELEASED)
                // --------------------
                using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill };
                using var fgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                using var selectionPaint = new SKPaint { Color = new SKColor(51, 153, 255, 100) };
                using var matchPaint = new SKPaint { Color = new SKColor(255, 255, 0, 100) };
                using var activeMatchPaint = new SKPaint { Color = new SKColor(255, 128, 0, 150) };

                // 1. Force Avalonia composition to completely wipe the reused SwapChain frame (destroys old ghost pixels)
                canvas.Clear(SKColors.Empty);

                // 2. Paint the terminal's theme background layer (with alpha) over the pristine canvas
                bgPaint.Color = frame.ThemeBg;
                bgPaint.BlendMode = SKBlendMode.SrcOver;
                canvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, bgPaint);
                IncrementRectDrawCall();

                float paddingLeft = FromDevicePx(_pixelGrid.OriginXPx);
                float paddingTop = FromDevicePx(_pixelGrid.OriginYPx);
                float[] colEdges = EnsureCellEdgeGrid(ref _colEdges, ref _colEdgesCount, bufferCols, _pixelGrid.OriginXPx, _pixelGrid.CellWidthPx);
                float[] rowEdges = EnsureCellEdgeGrid(ref _rowEdges, ref _rowEdgesCount, bufferRows, _pixelGrid.OriginYPx, _pixelGrid.CellHeightPx);

                int dirtyCells = 0;
                int buildsThisFrame = 0;
                int spanRenderCount = 0;
                int rowRenderCount = 0;
                int maxBuilds = MaxPictureBuildsPerFrame; // always unlimited

                // Pass 3: Text
                var tf = _skTypeface?.Typeface ?? SKTypeface.FromFamilyName(_typeface.FontFamily.Name);
                LogPrimaryFontDiagnostics(tf);
                using var font = (_skFont?.Font != null)
                    ? new SKFont(_skFont.Font.Typeface, _skFont.Font.Size)
                    : new SKFont(tf, (float)_fontSize);

                font.Edging = SKFontEdging.Antialias;

                // Draw rows in visual order. RowItems count == bufferRows by construction.
                for (int r = 0; r < frame.RowItems.Count; r++)
                {
                    var item = frame.RowItems[r];
                    var rowSpans = (r < spansByRow.Length) ? spansByRow[r] : null;

                    int rowTopPx = _pixelGrid.YForRowTop(r);
                    int baselinePx = _pixelGrid.YForBaseline(r);
                    float rowTopY = FromDevicePx(rowTopPx);
                    float baselineY = FromDevicePx(baselinePx);

                    if (item.CachedPicture != null)
                    {
                        canvas.DrawPicture(item.CachedPicture, 0, rowTopY);
                        IncrementOtherDrawCall();
                        dirtyCells += bufferCols;
                        continue;
                    }

                    if (item.Snapshot.HasValue)
                    {
                        var snapshot = item.Snapshot.Value;
                        bool hasDirtySpans = rowSpans != null && rowSpans.Count > 0;
                        bool shouldUseSpanRendering = hasDirtySpans && ShouldUseSpanRenderingForRow(rowSpans!, snapshot.Cols);

                        SKPicture? previousRowPicture = null;
                        if (shouldUseSpanRendering)
                        {
                            if (!(_rowCache?.TryGetLatestByRowId(snapshot.RowId, out previousRowPicture, out _) ?? false))
                            {
                                shouldUseSpanRendering = false;
                            }
                        }

                        if (shouldUseSpanRendering && previousRowPicture != null)
                        {
                            // Base row from previous cached revision, then repaint dirty spans only.
                            canvas.DrawPicture(previousRowPicture, 0, rowTopY);
                            IncrementOtherDrawCall();

                            int dirtyCellsInRow = 0;
                            for (int i = 0; i < rowSpans!.Count; i++)
                            {
                                var span = rowSpans[i];
                                int spanStart = Math.Max(0, span.ColStart - 1);
                                int spanEndExclusive = Math.Min(snapshot.Cols, span.ColEnd + 1);
                                if (spanEndExclusive <= spanStart)
                                {
                                    continue;
                                }

                                // OVERLAP FIX: Erase the dirty span bounding box before redrawing it.
                                // DrawRowTextFromSnapshot skips default background painting, so drawing over previousRowPicture 
                                // will leave ghost text mixed with the new text if not cleared.
                                float sx1 = GetColEdge(colEdges, spanStart, paddingLeft);
                                float sx2 = GetColEdge(colEdges, spanEndExclusive, paddingLeft);
                                float sH = FromDevicePx(_pixelGrid.CellHeightPx);
                                canvas.DrawRect(sx1, rowTopY, sx2 - sx1, sH, bgPaint);
                                IncrementRectDrawCall();

                                DrawRowTextFromSnapshot(
                                    canvas,
                                    snapshot,
                                    rowTopY: rowTopY,
                                    baselineY: baselineY,
                                    colEdges: colEdges,
                                    font,
                                    fgPaint,
                                    frame.ThemeFg,
                                    frame.ThemeBg,
                                    renderSnapshot.Theme,
                                    alpha,
                                    tf,
                                    drawBackgrounds: true,
                                    spanColStart: spanStart,
                                    spanColEndExclusive: spanEndExclusive);

                                spanRenderCount++;
                                dirtyCellsInRow += Math.Max(0, span.ColEnd - span.ColStart);
                            }

                            dirtyCells += dirtyCellsInRow;

                            // Refresh cache entry for the new revision (full-row picture for future frames).
                            // Skip when useRowCache=false (AltScreen) — no point recording a picture we won't store.
                            if (useRowCache && buildsThisFrame < maxBuilds)
                            {
                                var recSw = System.Diagnostics.Stopwatch.StartNew();
                                using var recorder = new SKPictureRecorder();
                                using var rowCanvas = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, FromDevicePx(_pixelGrid.CellHeightPx)));
                                DrawRowTextFromSnapshot(
                                    rowCanvas,
                                    snapshot,
                                    rowTopY: 0f,
                                    baselineY: FromDevicePx(_pixelGrid.BaselineOffsetPx),
                                    colEdges: colEdges,
                                    font,
                                    fgPaint,
                                    frame.ThemeFg,
                                    frame.ThemeBg,
                                    renderSnapshot.Theme,
                                    alpha,
                                    tf,
                                    drawBackgrounds: true);

                                var picture = recorder.EndRecording();
                                _rowCache!.Add(snapshot.RowId, snapshot.Revision, picture);
                                IncrementPictureBuild();

                                recSw.Stop();
                                RendererStatistics.RecordRowPictureRecorded();
                                RendererStatistics.RecordRowPictureRecordTime(recSw.ElapsedMilliseconds);
                                buildsThisFrame++;
                            }
                        }
                        else if (buildsThisFrame < maxBuilds)
                        {
                            rowRenderCount++;
                            if (useRowCache)
                            {
                                // Record into a picture so it can be cached and replayed on future frames.
                                var recSw = System.Diagnostics.Stopwatch.StartNew();
                                using var recorder = new SKPictureRecorder();
                                using var rowCanvas = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, FromDevicePx(_pixelGrid.CellHeightPx)));

                                DrawRowTextFromSnapshot(
                                    rowCanvas,
                                    snapshot,
                                    rowTopY: 0f,
                                    baselineY: FromDevicePx(_pixelGrid.BaselineOffsetPx),
                                    colEdges: colEdges,
                                    font,
                                    fgPaint,
                                    frame.ThemeFg,
                                    frame.ThemeBg,
                                    renderSnapshot.Theme,
                                    alpha,
                                    tf,
                                    drawBackgrounds: true);

                                var picture = recorder.EndRecording();
                                _rowCache!.Add(snapshot.RowId, snapshot.Revision, picture);
                                canvas.DrawPicture(picture, 0, rowTopY);
                                IncrementOtherDrawCall();
                                IncrementPictureBuild();

                                recSw.Stop();
                                RendererStatistics.RecordRowPictureRecorded();
                                RendererStatistics.RecordRowPictureRecordTime(recSw.ElapsedMilliseconds);
                                buildsThisFrame++;
                            }
                            else
                            {
                                // AltScreen / cache disabled: draw directly — no SKPicture allocation.
                                DrawRowTextFromSnapshot(
                                    canvas,
                                    snapshot,
                                    rowTopY: rowTopY,
                                    baselineY: baselineY,
                                    colEdges: colEdges,
                                    font,
                                    fgPaint,
                                    frame.ThemeFg,
                                    frame.ThemeBg,
                                    renderSnapshot.Theme,
                                    alpha,
                                    tf,
                                    drawBackgrounds: true);
                            }
                        }
                        else
                        {
                            rowRenderCount++;
                            // Budget exhausted: draw direct from snapshot (Correct BUT non-cached fallback)
                            DrawRowTextFromSnapshot(
                                canvas,
                                snapshot,
                                rowTopY: rowTopY,
                                baselineY: baselineY,
                                colEdges: colEdges,
                                font,
                                fgPaint,
                                frame.ThemeFg,
                                frame.ThemeBg,
                                renderSnapshot.Theme,
                                alpha,
                                tf,
                                drawBackgrounds: true);
                        }

                        if (!shouldUseSpanRendering)
                        {
                            dirtyCells += bufferCols;
                        }
                    }
                }

                // Pass 2: Images
                using (var imagePaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) })
                using (var imageBgPaint = new SKPaint { Color = frame.ThemeBg, Style = SKPaintStyle.Fill, IsAntialias = false })
                {
                    foreach (var img in frame.Images)
                    {
                        int visualY = img.CellY - absDisplayStart;
                        if (visualY + img.CellHeight > 0 && visualY < bufferRows)
                        {
                            float x = GetColEdge(colEdges, img.CellX, paddingLeft);
                            float x2 = GetColEdge(colEdges, img.CellX + img.CellWidth, paddingLeft);
                            float y = GetRowEdge(rowEdges, visualY, paddingTop);
                            float y2 = GetRowEdge(rowEdges, visualY + img.CellHeight, paddingTop);
                            float w = x2 - x;
                            float h = y2 - y;

                            var rect = new SKRect(x, y, x + w, y + h);

                            canvas.Save();
                            canvas.ClipRect(new SKRect(paddingLeft, 0, (float)Bounds.Width, (float)Bounds.Height));
                            // Clear image target region first so transparent/semi-transparent
                            // image pixels do not reveal previously drawn text layers.
                            canvas.DrawRect(rect, imageBgPaint);
                            IncrementRectDrawCall();
                            if (img.ImageHandle is SKBitmap bmp)
                            {
                                canvas.DrawBitmap(bmp, rect, imagePaint);
                                IncrementOtherDrawCall();
                            }
                            canvas.Restore();
                        }
                    }
                }

                // Pass 4: Overlays (Selection / Search)
                int selectionIndex = 0;
                int matchIndex = 0;
                var selectionRows = renderSnapshot.SelectionRows;
                var searchHighlights = renderSnapshot.SearchHighlights;
                for (int r = 0; r < bufferRows; r++)
                {
                    float y = FromDevicePx(_pixelGrid.YForRowTop(r));
                    float rowBottom = FromDevicePx(_pixelGrid.YForRowTop(r + 1));
                    float rowHeight = rowBottom - y;
                    int absRow = absDisplayStart + r;

                    while (selectionIndex < selectionRows.Length && selectionRows.Array[selectionIndex].AbsRow < absRow)
                    {
                        selectionIndex++;
                    }

                    int selTemp = selectionIndex;
                    while (selTemp < selectionRows.Length && selectionRows.Array[selTemp].AbsRow == absRow)
                    {
                        var sel = selectionRows.Array[selTemp];
                        float x1 = GetColEdge(colEdges, sel.ColStart, paddingLeft);
                        float x2 = GetColEdge(colEdges, sel.ColEnd + 1, paddingLeft);
                        canvas.DrawRect(x1, y, x2 - x1, rowHeight, selectionPaint);
                        IncrementRectDrawCall();
                        selTemp++;
                    }

                    // Sub-linear scan for search matches (assumes highlights are sorted by AbsRow)
                    while (matchIndex < searchHighlights.Length && searchHighlights.Array[matchIndex].AbsRow < absRow)
                    {
                        matchIndex++;
                    }

                    int tempIdx = matchIndex;
                    while (tempIdx < searchHighlights.Length && searchHighlights.Array[tempIdx].AbsRow == absRow)
                    {
                        var m = searchHighlights.Array[tempIdx];
                        var p = m.IsActive ? activeMatchPaint : matchPaint;
                        float x1 = GetColEdge(colEdges, m.StartCol, paddingLeft);
                        float x2 = GetColEdge(colEdges, m.EndCol + 1, paddingLeft);
                        canvas.DrawRect(x1, y, x2 - x1, rowHeight, p);
                        IncrementRectDrawCall();
                        tempIdx++;
                    }
                }

                // Cursor (optional policy: hide cursor when scrolled back)
                if (!_hideCursor && _scrollOffset == 0)
                {
                    int absCursorRow = (renderSnapshot.TotalLines - bufferRows) + renderSnapshot.CursorRow;
                    int visualRow = absCursorRow - absDisplayStart;

                    if (visualRow >= 0 && visualRow < bufferRows)
                    {
                        float x1 = FromDevicePx(_pixelGrid.XForCol(renderSnapshot.CursorCol));
                        float x2 = FromDevicePx(_pixelGrid.XForCol(renderSnapshot.CursorCol + 1));
                        int rowTopPx = _pixelGrid.YForRowTop(visualRow);
                        int rowBottomPx = _pixelGrid.YForRowTop(visualRow + 1);
                        float rowTop = FromDevicePx(rowTopPx);
                        float rowBottom = FromDevicePx(rowBottomPx);
                        float rowHeight = rowBottom - rowTop;
                        using var cursorPaint = new SKPaint { Color = frame.CursorColor, Style = SKPaintStyle.Fill };

                        switch (frame.CursorStyle)
                        {
                            case CursorStyle.Block:
                                canvas.DrawRect(x1, rowTop, x2 - x1, rowHeight, cursorPaint);
                                IncrementRectDrawCall();
                                break;
                            case CursorStyle.Beam:
                                {
                                    int beamWPx = Math.Max(1, (int)Math.Floor(_pixelGrid.CellWidthPx * 0.14));
                                    float beamW = FromDevicePx(beamWPx);
                                    canvas.DrawRect(x1, rowTop, beamW, rowHeight, cursorPaint);
                                    IncrementRectDrawCall();
                                    break;
                                }
                            case CursorStyle.Underline:
                            default:
                                {
                                    float uY = FromDevicePx(_pixelGrid.YForUnderline(visualRow));
                                    float underlineH = FromDevicePx(Math.Max(1, ToDevicePx(2)));
                                    canvas.DrawRect(x1, uY, x2 - x1, underlineH, cursorPaint);
                                    IncrementRectDrawCall();
                                    break;
                                }
                        }
                    }
                }

                if (GridDiagnosticsEnabled)
                {
                    DrawDebugCellGrid(canvas, colEdges, _colEdgesCount, rowEdges, _rowEdgesCount);
                }

                frameSw.Stop();
                RendererStatistics.RecordFrameRenderTime(frameSw.ElapsedMilliseconds);
                RendererStatistics.RecordFrame(fullRedraw: true, dirtyCells: dirtyCells);
                if (_collectFramePerfMetrics || _showRenderHud)
                {
                    _framePerfMetrics.DirtySpanCount = dirtySpanCount;
                    _framePerfMetrics.SpanRenderCount = spanRenderCount;
                    _framePerfMetrics.RowRenderCount = rowRenderCount;
                    _framePerfMetrics.DirtyCellsEstimated = dirtyCells;
                    _framePerfMetrics.FrameTimeMs = frameSw.Elapsed.TotalMilliseconds;
                    _framePerfMetrics.FrameIndex = RendererStatistics.TotalFrames;
                }

                if (_showRenderHud)
                {
                    DrawPerformanceHud(canvas, _framePerfMetrics, alpha);
                }

                CompleteFramePerfMetrics(perfWriter, frameSw.Elapsed.TotalMilliseconds, dirtyRowCount, dirtySpanCount);
                
                return renderSnapshot;
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("error.log", "\n--- Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
                throw;
            }
            finally
            {
                DisposeAndClearShapingCache();
                DisposeAndClearFallbackFontCache();
            }
        }

        private TerminalRenderSnapshot? DrawTerminal(SKCanvas canvas)
            => DrawTerminalInternal(canvas);

        private void DrawPerformanceHud(SKCanvas canvas, RenderPerfMetrics metrics, byte alpha)
        {
            float hudWidth = 280f;
            float hudHeight = 90f;
            float padding = 10f;
            float margin = 10f;

            // Top Right
            float x = (float)Bounds.Width - hudWidth - margin;
            float y = margin;

            using var bgPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, (byte)(alpha * 0.85f)),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(new SKRect(x, y, x + hudWidth, y + hudHeight), 4, 4, bgPaint);

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(100, 100, 100, alpha),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
            canvas.DrawRoundRect(new SKRect(x, y, x + hudWidth, y + hudHeight), 4, 4, borderPaint);

            using var textPaint = new SKPaint
            {
                Color = new SKColor(0, 255, 0, alpha),
                Typeface = SKTypeface.Default,
                TextSize = 12f,
                IsAntialias = true
            };

            float textX = x + padding;
            float textY = y + padding + 12f;
            float lineHeight = 16f;

            canvas.DrawText($"Frame: {metrics.FrameTimeMs:F2} ms", textX, textY, textPaint);
            textY += lineHeight;
            canvas.DrawText($"Dirty Cells: {metrics.DirtyCellsEstimated} | Rows: {metrics.DirtyRows}", textX, textY, textPaint);
            textY += lineHeight;
            canvas.DrawText($"Draws: {metrics.DrawCallsTotal} (Cache:{metrics.RowPictureCacheHits}/{metrics.RowPictureCacheMisses})", textX, textY, textPaint);
            textY += lineHeight;
            canvas.DrawText($"Atlas Builds: {metrics.AtlasAlphaGlyphs}/{metrics.AtlasColorGlyphs} | Mem: {metrics.AllocBytesThisFrame/1024.0:F1} kb", textX, textY, textPaint);
        }

        private void FlushBatches(SKCanvas canvas)
        {
            if (_glyphCache == null) return;

            var (alphaAtlas, colorAtlas) = _glyphCache.GetAtlasImages();
            int alphaGlyphs = _alphaCount;
            int colorGlyphs = _colorCount;
            int atlasDrawCalls = 0;

            if (alphaGlyphs > 0)
            {
                var alphaRectsScratch = EnsureScratchCapacity(ref _alphaRectsScratch, alphaGlyphs);
                var alphaXformsScratch = EnsureScratchCapacity(ref _alphaXformsScratch, alphaGlyphs);
                var alphaColorsScratch = EnsureScratchCapacity(ref _alphaColorsScratch, alphaGlyphs);
                Array.Copy(_alphaRects, alphaRectsScratch, alphaGlyphs);
                Array.Copy(_alphaXforms, alphaXformsScratch, alphaGlyphs);
                Array.Copy(_alphaColors, alphaColorsScratch, alphaGlyphs);
                if (_alphaScratchPrevCount > alphaGlyphs)
                {
                    int tailLength = _alphaScratchPrevCount - alphaGlyphs;
                    Array.Clear(alphaRectsScratch, alphaGlyphs, tailLength);
                    Array.Clear(alphaXformsScratch, alphaGlyphs, tailLength);
                    Array.Clear(alphaColorsScratch, alphaGlyphs, tailLength);
                }
                _alphaScratchPrevCount = alphaGlyphs;

                _atlasAlphaPaint.Style = SKPaintStyle.Fill;
                _atlasAlphaPaint.IsAntialias = false;
                canvas.DrawAtlas(
                    alphaAtlas,
                    alphaRectsScratch,
                    alphaXformsScratch,
                    alphaColorsScratch,
                    SKBlendMode.Modulate,
                    new SKSamplingOptions(SKFilterMode.Nearest),
                    _atlasAlphaPaint);
                atlasDrawCalls++;

                _alphaCount = 0;
            }

            if (colorGlyphs > 0)
            {
                var colorRectsScratch = EnsureScratchCapacity(ref _colorRectsScratch, colorGlyphs);
                var colorXformsScratch = EnsureScratchCapacity(ref _colorXformsScratch, colorGlyphs);
                Array.Copy(_colorRects, colorRectsScratch, colorGlyphs);
                Array.Copy(_colorXforms, colorXformsScratch, colorGlyphs);
                if (_colorScratchPrevCount > colorGlyphs)
                {
                    int tailLength = _colorScratchPrevCount - colorGlyphs;
                    Array.Clear(colorRectsScratch, colorGlyphs, tailLength);
                    Array.Clear(colorXformsScratch, colorGlyphs, tailLength);
                }
                _colorScratchPrevCount = colorGlyphs;

                _atlasColorPaint.Style = SKPaintStyle.Fill;
                _atlasColorPaint.IsAntialias = false;
                canvas.DrawAtlas(
                    colorAtlas,
                    colorRectsScratch,
                    colorXformsScratch,
                    null,
                    SKBlendMode.SrcOver,
                    new SKSamplingOptions(SKFilterMode.Linear),
                    _atlasColorPaint);
                atlasDrawCalls++;

                _colorCount = 0;
            }

            if (atlasDrawCalls > 0)
            {
                RecordFlush(alphaGlyphs, colorGlyphs, atlasDrawCalls);
            }
        }

        private void AddAlphaBatchEntry(SKRect rect, SKRotationScaleMatrix xform, SKColor color)
        {
            EnsureAlphaBatchCapacity(_alphaCount + 1);
            int index = _alphaCount++;
            _alphaRects[index] = rect;
            _alphaXforms[index] = xform;
            _alphaColors[index] = color;
        }

        private void AddColorBatchEntry(SKRect rect, SKRotationScaleMatrix xform)
        {
            EnsureColorBatchCapacity(_colorCount + 1);
            int index = _colorCount++;
            _colorRects[index] = rect;
            _colorXforms[index] = xform;
        }

        private void EnsureAlphaBatchCapacity(int requiredCount)
        {
            if (_alphaRects.Length >= requiredCount)
            {
                return;
            }

            int current = _alphaRects.Length;
            int newCapacity = Math.Max(requiredCount, Math.Max(InitialAtlasBatchCapacity, current == 0 ? InitialAtlasBatchCapacity : current * 2));
            Array.Resize(ref _alphaRects, newCapacity);
            Array.Resize(ref _alphaXforms, newCapacity);
            Array.Resize(ref _alphaColors, newCapacity);
        }

        private void EnsureColorBatchCapacity(int requiredCount)
        {
            if (_colorRects.Length >= requiredCount)
            {
                return;
            }

            int current = _colorRects.Length;
            int newCapacity = Math.Max(requiredCount, Math.Max(InitialAtlasBatchCapacity, current == 0 ? InitialAtlasBatchCapacity : current * 2));
            Array.Resize(ref _colorRects, newCapacity);
            Array.Resize(ref _colorXforms, newCapacity);
        }

        private static T[] EnsureScratchCapacity<T>(ref T[]? buffer, int requiredCount)
        {
            if (requiredCount <= 0)
            {
                return buffer ??= Array.Empty<T>();
            }

            if (buffer == null)
            {
                buffer = new T[Math.Max(InitialAtlasBatchCapacity, requiredCount)];
                return buffer;
            }

            if (buffer.Length >= requiredCount)
            {
                return buffer;
            }

            int newCapacity = buffer.Length == 0 ? InitialAtlasBatchCapacity : buffer.Length;
            while (newCapacity < requiredCount)
            {
                newCapacity = Math.Max(InitialAtlasBatchCapacity, newCapacity * 2);
            }

            Array.Resize(ref buffer, newCapacity);
            return buffer;
        }

        private SKFont GetOrCreateFallbackFont(SKTypeface tf)
        {
            if (_fallbackFontCache.TryGetValue(tf, out var cached))
            {
                return cached;
            }

            var created = new SKFont(tf, (float)_fontSize) { Edging = SKFontEdging.Antialias };
            _fallbackFontCache[tf] = created;
            return created;
        }

        private ShapingResources GetOrCreateShapingResources(SKTypeface tf)
        {
            if (_shapingCache.TryGetValue(tf, out var cached))
            {
                return cached;
            }

            var font = new SKFont(tf, (float)_fontSize) { Edging = SKFontEdging.Antialias };
            var shaper = new SKShaper(tf);
            var created = new ShapingResources(shaper, font);
            _shapingCache[tf] = created;
            return created;
        }

        private void DisposeAndClearFallbackFontCache()
        {
            if (_fallbackFontCache.Count == 0)
            {
                return;
            }

            foreach (var cached in _fallbackFontCache.Values)
            {
                cached.Dispose();
            }

            _fallbackFontCache.Clear();
        }

        private void DisposeAndClearShapingCache()
        {
            if (_shapingCache.Count == 0)
            {
                return;
            }

            foreach (var resources in _shapingCache.Values)
            {
                resources.Shaper.Dispose();
                resources.Font.Dispose();
            }

            _shapingCache.Clear();
        }

        private int DrawRowTextFromSnapshot(
            SKCanvas canvas,
            RenderRowSnapshot snapshot,
            float rowTopY,
            float baselineY,
            float[] colEdges,
            SKFont font,
            SKPaint fgPaint,
            SKColor themeFg,
            SKColor themeBg,
            RenderThemeSnapshot themeSnapshot,
            byte alpha,
            SKTypeface primaryTf,
            bool drawBackgrounds,
            int spanColStart = 0,
            int spanColEndExclusive = int.MaxValue)
        {
            const float paddingLeft = 4f;
            int cellsRendered = 0;
            float snappedCellHeight = FromDevicePx(_cellHeightDevicePx);
            int rowTopPx = ToDevicePx(rowTopY);
            int baselinePx = ToDevicePx(baselineY);
            int rowIndex = _pixelGrid.CellHeightPx > 0 ? (rowTopPx - _pixelGrid.OriginYPx) / _pixelGrid.CellHeightPx : 0;
            float rowTopYDip = FromDevicePx(rowTopPx);
            float baselineYDip = FromDevicePx(baselinePx);
            int effectiveSpanStart = Math.Clamp(spanColStart, 0, snapshot.Cols);
            int effectiveSpanEnd = Math.Clamp(spanColEndExclusive, effectiveSpanStart, snapshot.Cols);
            if (effectiveSpanEnd <= effectiveSpanStart)
            {
                return 0;
            }

            bool hasSpanClip = effectiveSpanStart > 0 || effectiveSpanEnd < snapshot.Cols;
            if (hasSpanClip)
            {
                float clipX1 = FromDevicePx(_pixelGrid.XForCol(effectiveSpanStart));
                float clipX2 = FromDevicePx(_pixelGrid.XForCol(effectiveSpanEnd));
                canvas.Save();
                canvas.ClipRect(new SKRect(clipX1, rowTopYDip, clipX2, rowTopYDip + snappedCellHeight));
            }

            try
            {
                if (_runBuilder.Capacity > RunBuilderMaxCapacity)
                {
                    // Guard against permanently retaining very large capacities after outlier runs.
                    _runBuilder.Clear();
                    _runBuilder.Capacity = RunBuilderInitialCapacity;
                }

                var runBuilder = _runBuilder;

                for (int c = 0; c < snapshot.Cols; c++)
                {
                    var cell = snapshot.Cells[c];
                    if (cell.IsWideContinuation || cell.IsHidden) continue;

                    var cb = ResolveCellBackground(cell, themeBg, alpha, themeSnapshot);
                    var cf = ResolveCellForeground(cell, themeFg, alpha, themeSnapshot);
                    bool runIsItalic = cell.IsItalic;
                    bool runIsUnderline = cell.IsUnderline;
                    bool runIsStrikethrough = cell.IsStrikethrough;
                    bool runIsFaint = cell.IsFaint;
                    var fg = cell.IsInverse ? cb : cf;
                    var bg = cell.IsInverse ? cf : cb;
                    if (runIsFaint)
                    {
                        fg = BlendTowards(fg, bg, 0.5f);
                    }

                    runBuilder.Clear();
                    int totalRunWidth = 0;
                    bool runNeedsComplexShaping = false;
                    bool runHasBoxDrawing = false;
                    bool runHasComplexShapingGlyph = false;
                    int k = c;

                    while (k < snapshot.Cols)
                    {
                        var next = snapshot.Cells[k];
                        if (next.IsHidden || next.IsWideContinuation)
                        {
                            k++;
                            continue;
                        }

                        var ncb = ResolveCellBackground(next, themeBg, alpha, themeSnapshot);
                        var ncf = ResolveCellForeground(next, themeFg, alpha, themeSnapshot);
                        var nfg = next.IsInverse ? ncb : ncf;
                        var nbg = next.IsInverse ? ncf : ncb;
                        if (next.IsFaint)
                        {
                            nfg = BlendTowards(nfg, nbg, 0.5f);
                        }

                        if (nfg != fg || nbg != bg)
                            break;
                        if (next.IsItalic != runIsItalic ||
                            next.IsUnderline != runIsUnderline ||
                            next.IsStrikethrough != runIsStrikethrough ||
                            next.IsFaint != runIsFaint)
                            break;

                        string cellText = NormalizeTextElementForRender(next.Text ?? next.Character.ToString());
                        bool cellHasBoxDrawing = ContainsBoxDrawing(cellText);
                        bool cellNeedsComplexShaping = _enableComplexShaping && ContainsRunesRequiringComplexShaping(cellText);

                        if (k > c)
                        {
                            // Keep box-drawing runs isolated from icon/complex-shaping runs so
                            // snapped box primitives are not bypassed by mixed run shaping.
                            if ((runHasBoxDrawing && cellNeedsComplexShaping) ||
                                (runHasComplexShapingGlyph && cellHasBoxDrawing))
                            {
                                break;
                            }

                            // Keep complex-script shaping runs isolated from plain LTR runs.
                            // Mixing prefixes like "Arabic: " with Arabic text in a single shaper
                            // run can produce incorrect bidi/shaping output.
                            if ((runHasComplexShapingGlyph && !cellNeedsComplexShaping) ||
                                (!runHasComplexShapingGlyph && cellNeedsComplexShaping))
                            {
                                break;
                            }
                        }

                        runHasBoxDrawing |= cellHasBoxDrawing;
                        runHasComplexShapingGlyph |= cellNeedsComplexShaping;
                        runNeedsComplexShaping = runHasComplexShapingGlyph && !runHasBoxDrawing;

                        runBuilder.Append(cellText);
                        int w = GetSafeGraphemeWidth(cellText); // GetGraphemeWidth is thread-safe
                        totalRunWidth += w;
                        k++;
                    }

                    int runStartCol = c;
                    int runEndCol = c + totalRunWidth;
                    if (runEndCol <= effectiveSpanStart)
                    {
                        c = k - 1;
                        continue;
                    }

                    if (runStartCol >= effectiveSpanEnd)
                    {
                        break;
                    }

                    string runText = runBuilder.ToString();
                    float rx = FromDevicePx(_pixelGrid.XForCol(c));
                    float rx2 = FromDevicePx(_pixelGrid.XForCol(c + totalRunWidth));
                    fgPaint.Color = fg;
                    float rw = rx2 - rx;
                    float strokeWidth = Math.Max(1f, (float)(_metrics.CellHeight * 0.06));

                    // Backgrounds (when requested)
                    if (drawBackgrounds && bg != themeBg && bg.Alpha != 0)
                    {
                        _bgFillPaint.Color = bg;
                        _bgFillPaint.Style = SKPaintStyle.Fill;
                        _bgFillPaint.IsAntialias = false;
                        canvas.DrawRect(rx, rowTopYDip, rw, snappedCellHeight, _bgFillPaint);
                        IncrementRectDrawCall();
                    }

                    bool appliedItalicTransform = false;
                    if (runIsItalic)
                    {
                        FlushBatches(canvas);
                        canvas.Save();
                        canvas.Translate(rx, baselineYDip);
                        canvas.Skew(-0.22f, 0f);
                        canvas.Translate(-rx, -baselineYDip);
                        appliedItalicTransform = true;
                    }

                    if (runNeedsComplexShaping)
                    {
                        FlushBatches(canvas);
                        int fallbackChar = FindFirstMissingGlyphCodePoint(runText, primaryTf);
                        SKTypeface tfToUse = fallbackChar != 0
                            ? ResolveTypefaceForCodePoint(fallbackChar, primaryTf)
                            : primaryTf;

                    bool useLayer = totalRunWidth > 1;
                    if (useLayer)
                    {
                        canvas.SaveLayer(new SKRect(rx, rowTopYDip, rx + rw, rowTopYDip + (float)_metrics.CellHeight), null);
                    }

                    var shapingResources = GetOrCreateShapingResources(tfToUse ?? primaryTf);
                    var shapedFont = shapingResources.Font;
                    var shaper = shapingResources.Shaper;
                    LogBoxRunDiagnostics(runText, totalRunWidth, shapedFont, shapedFont.MeasureText(runText));
                    canvas.DrawShapedText(shaper, runText, rx, baselineYDip, shapedFont, fgPaint);
                    IncrementShapedTextRun();
                    IncrementTextDrawCall();

                    if (useLayer) canvas.Restore();
                    cellsRendered += totalRunWidth;
                }
                else
                {
                    if (_glyphCache != null && !runIsItalic)
                    {
                        if (ShouldDrawRunDirectWhenGlyphCacheEnabled(runText))
                        {
                            FlushBatches(canvas);
                            canvas.DrawText(runText, rx, baselineYDip, font, fgPaint);
                            IncrementTextDrawCall();
                            IncrementDirectDrawTextCall();
                            cellsRendered += totalRunWidth;
                            c = k - 1;
                            continue;
                        }

                        int cellX = c;
                        float yBaselineSnap = baselineYDip;
                        float glyphY = FromDevicePx(ToDevicePx(yBaselineSnap + font.Metrics.Ascent));
                        float invScale = (float)(1.0 / _renderScaling);
                        _blockFillPaint.Color = fg;
                        _blockFillPaint.Style = SKPaintStyle.Fill;
                        _blockFillPaint.IsAntialias = false;

                        foreach (var rune in runText.EnumerateRunes())
                        {
                            string grapheme = rune.ToString();
                            int graphemeWidth = GetSafeGraphemeWidth(grapheme);
                            float xIdxSnap = FromDevicePx(_pixelGrid.XForCol(cellX));

                            int fallbackChar = FindFirstMissingGlyphCodePoint(grapheme, primaryTf);
                            SKFont glyphFont = font;
                            if (fallbackChar != 0)
                            {
                                var glyphTf = ResolveTypefaceForCodePoint(fallbackChar, primaryTf);
                                if (glyphTf != primaryTf)
                                {
                                    glyphFont = GetOrCreateFallbackFont(glyphTf);
                                }
                            }

                            if (GlyphDiagnosticsEnabled && IsSingleRuneInRange(grapheme, 0x2500, 0x257F))
                            {
                                float measuredGlyphWidth = glyphFont.MeasureText(grapheme);
                                float expectedGlyphWidth = graphemeWidth * _metrics.CellWidth;
                                string glyphFamily = glyphFont.Typeface?.FamilyName ?? "unknown";
                                string diagKey = $"boxglyph:{glyphFamily}:{grapheme}";
                                string diagMsg = $"[GlyphDiag] box-glyph='{grapheme}' font='{glyphFamily}' measuredDip={measuredGlyphWidth:F4} expectedDip={expectedGlyphWidth:F4} delta={measuredGlyphWidth - expectedGlyphWidth:F4}";
                                LogDiagOnce(diagKey, diagMsg);
                            }

                            float cellX1 = xIdxSnap;
                            float cellX2 = FromDevicePx(_pixelGrid.XForCol(cellX + graphemeWidth));
                            float cellW = cellX2 - cellX1;
                            float cellH = snappedCellHeight;
                            bool hasSingleRune = TryGetSingleRuneCodePoint(grapheme, out int graphemeCodePoint);
                            bool isBlockShadeOrQuadrant = hasSingleRune && graphemeCodePoint >= 0x2580 && graphemeCodePoint <= 0x259F;
                            bool isBrailleGlyph = hasSingleRune && graphemeCodePoint >= 0x2800 && graphemeCodePoint <= 0x28FF;
                            bool isBlackSquareGlyph = hasSingleRune && graphemeCodePoint == 0x25A0;
                            bool graphGlyphMissing = false;
                            if ((isBlockShadeOrQuadrant || isBrailleGlyph || isBlackSquareGlyph) &&
                                (glyphFont.Typeface == null || !glyphFont.Typeface.ContainsGlyph(graphemeCodePoint)))
                            {
                                graphGlyphMissing = true;
                            }
                            bool usePrimitiveBlockLike = IsBlockElementPrimitiveRenderingEnabled() || isBlockShadeOrQuadrant || isBlackSquareGlyph || graphGlyphMissing;
                            bool usePrimitiveBraille = IsBlockElementPrimitiveRenderingEnabled() || graphGlyphMissing;

                            // Prefer primitives for block/shade/quadrant bar glyphs to guarantee
                            // seam-free cell fills. For braille, keep font rendering unless
                            // primitives are enabled or the current typeface lacks the glyph.
                            if (usePrimitiveBlockLike &&
                                TryGetBlockFillRect(grapheme, out int xStartEighths, out int xEndEighths, out int yStartEighths, out int yEndEighths))
                            {
                                FlushBatches(canvas);

                                float fillX1 = xStartEighths == 0 ? cellX1 : SnapX(cellX1 + (cellW * (xStartEighths / 8f)));
                                float fillX2 = xEndEighths == 8 ? cellX2 : SnapX(cellX1 + (cellW * (xEndEighths / 8f)));
                                float rowBottom = rowTopYDip + cellH;
                                float fillY1 = yStartEighths == 0 ? rowTopYDip : SnapY(rowTopYDip + (cellH * (yStartEighths / 8f)));
                                float fillY2 = yEndEighths == 8 ? rowBottom : SnapY(rowTopYDip + (cellH * (yEndEighths / 8f)));
                                if (fillX2 > fillX1 && fillY2 > fillY1)
                                {
                                    canvas.DrawRect(fillX1, fillY1, fillX2 - fillX1, fillY2 - fillY1, _blockFillPaint);
                                    IncrementRectDrawCall();
                                }

                                cellX += graphemeWidth;
                                continue;
                            }

                            if (usePrimitiveBlockLike && TryGetShadeFillAlpha(grapheme, out float shadeAlpha))
                            {
                                FlushBatches(canvas);
                                byte shadeA = (byte)Math.Clamp((int)Math.Round(fg.Alpha * shadeAlpha), 0, 255);
                                _shadeFillPaint.Color = new SKColor(fg.Red, fg.Green, fg.Blue, shadeA);
                                _shadeFillPaint.Style = SKPaintStyle.Fill;
                                _shadeFillPaint.IsAntialias = false;
                                if (cellW > 0 && cellH > 0)
                                {
                                    canvas.DrawRect(cellX1, rowTopYDip, cellW, cellH, _shadeFillPaint);
                                    IncrementRectDrawCall();
                                }

                                cellX += graphemeWidth;
                                continue;
                            }

                            if (usePrimitiveBlockLike && TryGetQuadrantFillMask(grapheme, out byte quadrantMask))
                            {
                                FlushBatches(canvas);
                                DrawQuadrantSubcells(canvas, quadrantMask, cellX1, rowTopYDip, cellW, cellH, _blockFillPaint);
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (usePrimitiveBraille && TryGetBraillePattern(grapheme, out byte brailleMask))
                            {
                                FlushBatches(canvas);
                                DrawBrailleSubcells(canvas, brailleMask, cellX1, rowTopYDip, cellW, cellH, _blockFillPaint);
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (IsBoxDrawingPrimitiveRenderingEnabled() &&
                                TryGetSingleRuneCodePoint(grapheme, out int cpBox) &&
                                cpBox >= 0x2500 && cpBox <= 0x257F)
                            {
                                FlushBatches(canvas);
                                if (TryDrawBoxDrawingGlyph(canvas, grapheme, cellX1, rowTopYDip, cellW, cellH, fg))
                                {
                                    cellX += graphemeWidth;
                                    continue;
                                }
                            }

                            if (ShouldForceDirectTextForGraphGlyph(grapheme))
                            {
                                FlushBatches(canvas);
                                DrawDirectGrapheme(
                                    canvas,
                                    grapheme,
                                    xIdxSnap,
                                    yBaselineSnap,
                                    glyphFont,
                                    fgPaint,
                                    cellX1,
                                    rowTopYDip,
                                    cellW,
                                    cellH);
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (ShouldBypassGlyphAtlasForGrapheme(grapheme))
                            {
                                FlushBatches(canvas);
                                DrawDirectGrapheme(
                                    canvas,
                                    grapheme,
                                    xIdxSnap,
                                    yBaselineSnap,
                                    glyphFont,
                                    fgPaint,
                                    cellX1,
                                    rowTopYDip,
                                    cellW,
                                    cellH);
                                cellX += graphemeWidth;
                                continue;
                            }

                            var cached = _glyphCache.GetOrAdd(grapheme, glyphFont, (float)_renderScaling);

                            if (cached != null)
                            {
                                var (rect, type) = cached.Value;
                                var xform = SKRotationScaleMatrix.Create(invScale, 0, xIdxSnap, glyphY, 0, 0);
                                if (type == AtlasType.Alpha8)
                                {
                                    AddAlphaBatchEntry(rect, xform, fg);
                                }
                                else
                                {
                                    AddColorBatchEntry(rect, xform);
                                }
                            }
                            else
                            {
                                FlushBatches(canvas);
                                canvas.DrawText(grapheme, xIdxSnap, yBaselineSnap, glyphFont, fgPaint);
                                IncrementTextDrawCall();
                                IncrementDirectDrawTextCall();
                            }
                            cellX += graphemeWidth;
                        }
                        LogBoxRunDiagnostics(runText, totalRunWidth, font, font.MeasureText(runText));
                    }
                    else
                    {
                        canvas.DrawText(runText, rx, baselineYDip, font, fgPaint);
                        IncrementTextDrawCall();
                        IncrementDirectDrawTextCall();
                    }
                    cellsRendered += totalRunWidth;
                }

                if (appliedItalicTransform)
                {
                    canvas.Restore();
                }

                bool underlineHasVisibleText = runIsUnderline && ContainsNonWhitespace(runText);
                if (underlineHasVisibleText || runIsStrikethrough)
                {
                    _decoStrokePaint.Color = fg;
                    _decoStrokePaint.IsAntialias = true;
                    _decoStrokePaint.Style = SKPaintStyle.Stroke;
                    _decoStrokePaint.StrokeWidth = strokeWidth;

                    if (underlineHasVisibleText)
                    {
                        float underlineX1 = rx;
                        float underlineX2 = rx + rw;
                        if (TryGetUnderlineBounds(runText, c, colEdges, paddingLeft, out float trimmedX1, out float trimmedX2))
                        {
                            underlineX1 = trimmedX1;
                            underlineX2 = trimmedX2;
                        }

                        float underlineY = FromDevicePx(_pixelGrid.YForUnderline(rowIndex));
                        canvas.DrawLine(underlineX1, underlineY, underlineX2, underlineY, _decoStrokePaint);
                        IncrementRectDrawCall();
                    }

                    if (runIsStrikethrough)
                    {
                        float strikeY = FromDevicePx(_pixelGrid.YForStrike(rowIndex));
                        canvas.DrawLine(rx, strikeY, rx + rw, strikeY, _decoStrokePaint);
                        IncrementRectDrawCall();
                    }
                }

                    c = k - 1;
                }

                FlushBatches(canvas);
                return cellsRendered;
            }
            finally
            {
                if (hasSpanClip)
                {
                    canvas.Restore();
                }
            }
        }

        private static bool ShouldUseSpanRenderingForRow(List<DirtySpan> rowSpans, int rowCols)
        {
            if (rowSpans.Count == 0 || rowCols <= 0)
            {
                return false;
            }

            if (rowSpans.Count > SpanRenderMaxSpansPerRow)
            {
                return false;
            }

            int covered = 0;
            for (int i = 0; i < rowSpans.Count; i++)
            {
                covered += Math.Max(0, rowSpans[i].ColEnd - rowSpans[i].ColStart);
            }

            return covered < (int)Math.Ceiling(rowCols * SpanRenderCoverageFallbackThreshold);
        }

        private int DrawRowText(SKCanvas canvas, int absRow, int bufferCols, float rowTopY, float baselineY, SKFont font, SKPaint fgPaint, SKColor themeFg, SKColor themeBg, byte alpha, SKTypeface primaryTf, bool drawBackgrounds)
        {
            // [DEPRECATED] Only kept as legacy until end of Step 4 if needed.
            // But we already switched all call sites to FromSnapshot.
            return 0;
        }

        private static bool IsEnvFlagEnabled(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBoxDrawingPrimitiveRenderingEnabled()
        {
            TestPrimitiveRenderOverride? overrideFlags = PrimitiveRenderOverrideForTests.Value;
            return overrideFlags?.UseBoxDrawingPrimitives ?? UseBoxDrawingPrimitives;
        }

        private static bool IsBlockElementPrimitiveRenderingEnabled()
        {
            TestPrimitiveRenderOverride? overrideFlags = PrimitiveRenderOverrideForTests.Value;
            return overrideFlags?.UseBlockElementPrimitives ?? UseBlockElementPrimitives;
        }

        internal static IDisposable PushPrimitiveRenderingOverrideForTests(bool useBoxDrawingPrimitives, bool useBlockElementPrimitives)
        {
            TestPrimitiveRenderOverride? previous = PrimitiveRenderOverrideForTests.Value;
            PrimitiveRenderOverrideForTests.Value = new TestPrimitiveRenderOverride
            {
                UseBoxDrawingPrimitives = useBoxDrawingPrimitives,
                UseBlockElementPrimitives = useBlockElementPrimitives
            };

            return new PrimitiveRenderOverrideScope(previous);
        }

        internal static void ResetRenderPerfWriterForTests()
        {
            if (SharedRenderPerfWriter.IsValueCreated)
            {
                try
                {
                    SharedRenderPerfWriter.Value?.Dispose();
                }
                catch
                {
                    // Keep test helper non-fatal.
                }
            }

            SharedRenderPerfWriter = new Lazy<RenderPerfWriter?>(RenderPerfWriter.CreateFromEnvironment);
        }

        private RenderPerfWriter? BeginFramePerfMetrics()
        {
            RenderPerfWriter? writer = SharedRenderPerfWriter.Value;
            if (writer == null)
            {
                _collectFramePerfMetrics = false;
                _framePerfMetrics = default;
                _frameOtherDrawCalls = 0;
                _frameAllocStartBytes = 0;
                return null;
            }

            _collectFramePerfMetrics = true;
            _framePerfMetrics = default;
            _frameOtherDrawCalls = 0;
            _framePerfMetrics.FrameIndex = writer.NextFrameIndex();
            _frameAllocStartBytes = GC.GetAllocatedBytesForCurrentThread();
            return writer;
        }

        private void CompleteFramePerfMetrics(RenderPerfWriter? writer, double frameTimeMs, int dirtyRows, int dirtySpans)
        {
            if (!_collectFramePerfMetrics || writer == null)
            {
                return;
            }

            _framePerfMetrics.FrameTimeMs = frameTimeMs;
            _framePerfMetrics.DirtyRows = dirtyRows;
            _framePerfMetrics.DirtySpansTotal = dirtySpans;

            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - _frameAllocStartBytes;
            _framePerfMetrics.AllocBytesThisFrame = allocDelta > 0 ? allocDelta : 0;
            _framePerfMetrics.DrawCallsTotal = _framePerfMetrics.DrawCallsText + _framePerfMetrics.DrawCallsRects + _frameOtherDrawCalls;
            writer.TryWrite(_framePerfMetrics);
        }

        private void IncrementRowPictureCacheHit()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.RowPictureCacheHits++;
            }
        }

        private void IncrementRowPictureCacheMiss()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.RowPictureCacheMisses++;
            }
        }

        private void IncrementPictureBuild()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.PictureBuilds++;
            }
        }

        private void IncrementRectDrawCall()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.DrawCallsRects++;
            }
        }

        private void IncrementTextDrawCall()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.DrawCallsText++;
            }
        }

        private void IncrementDirectDrawTextCall()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.DirectDrawTextCount++;
            }
        }

        private void IncrementShapedTextRun()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _framePerfMetrics.ShapedTextRuns++;
            }
        }

        private void IncrementOtherDrawCall()
        {
            if (_collectFramePerfMetrics || _showRenderHud)
            {
                _frameOtherDrawCalls++;
            }
        }

        private void RecordFlush(int alphaGlyphs, int colorGlyphs, int atlasDrawCalls)
        {
            if (!_collectFramePerfMetrics && !_showRenderHud)
            {
                return;
            }

            _framePerfMetrics.FlushCount++;
            _framePerfMetrics.AtlasAlphaGlyphs += alphaGlyphs;
            _framePerfMetrics.AtlasColorGlyphs += colorGlyphs;
            _frameOtherDrawCalls += atlasDrawCalls;
        }

        private int ToDevicePx(double logical)
            => (int)Math.Round(logical * _renderScaling, MidpointRounding.AwayFromZero);

        private float FromDevicePx(int px)
            => (float)(px / _renderScaling);

        private float Snap(double logical)
            => (float)(Math.Round(logical * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

        private float SnapX(double logicalX) => Snap(logicalX);

        private float SnapY(double logicalY) => Snap(logicalY);

        private float[] EnsureCellEdgeGrid(ref float[]? buffer, ref int usedCount, int cellCount, int originPx, int deviceCellSize)
        {
            int count = Math.Max(0, cellCount);
            int required = count + 1;
            if (buffer == null || buffer.Length < required)
            {
                var rented = ArrayPool<float>.Shared.Rent(required);
                if (buffer != null)
                {
                    ArrayPool<float>.Shared.Return(buffer, clearArray: false);
                }

                buffer = rented;
            }

            usedCount = required;
            for (int i = 0; i <= count; i++)
            {
                buffer[i] = FromDevicePx(originPx + (i * deviceCellSize));
            }
            return buffer;
        }

        private float GetColEdge(float[] colEdges, int colIndex, float paddingLeft)
        {
            if ((uint)colIndex < (uint)colEdges.Length) return colEdges[colIndex];
            return FromDevicePx(_pixelGrid.XForCol(colIndex));
        }

        private float GetRowEdge(float[] rowEdges, int rowIndex, float paddingTop)
        {
            if ((uint)rowIndex < (uint)rowEdges.Length) return rowEdges[rowIndex];
            return FromDevicePx(_pixelGrid.YForRowTop(rowIndex));
        }

        private void DrawDebugCellGrid(SKCanvas canvas, float[] colEdges, int colEdgeCount, float[] rowEdges, int rowEdgeCount)
        {
            using var gridPaint = new SKPaint
            {
                Color = new SKColor(0, 200, 255, 72),
                Style = SKPaintStyle.Stroke,
                IsAntialias = false,
                StrokeWidth = Math.Max(1f, FromDevicePx(1))
            };

            float y1 = rowEdgeCount > 0 ? rowEdges[0] : 0f;
            float y2 = rowEdgeCount > 0 ? rowEdges[rowEdgeCount - 1] : (float)Bounds.Height;
            for (int i = 0; i < colEdgeCount; i++)
            {
                float x = colEdges[i];
                canvas.DrawLine(x, y1, x, y2, gridPaint);
            }
        }

        private void ReturnEdgeBuffers()
        {
            if (_colEdges != null)
            {
                ArrayPool<float>.Shared.Return(_colEdges);
                _colEdges = null;
                _colEdgesCount = 0;
            }

            if (_rowEdges != null)
            {
                ArrayPool<float>.Shared.Return(_rowEdges);
                _rowEdges = null;
                _rowEdgesCount = 0;
            }
        }

        private void LogDiagOnce(string key, string message)
        {
            if (!GlyphDiagnosticsEnabled) return;
            if (GlyphDiagOnce.TryAdd(key, 0))
            {
                TerminalLogger.Log(message);
            }
        }

        private void LogPrimaryFontDiagnostics(SKTypeface primaryTf)
        {
            if (!GlyphDiagnosticsEnabled) return;
            string key = $"fontcfg:{_typeface.FontFamily.Name}:{primaryTf.FamilyName}:{_fontSize}:{_renderScaling}";
            string message = $"[GlyphDiag] configured='{_typeface.FontFamily.Name}' primary='{primaryTf.FamilyName}' size={_fontSize:F2} scaling={_renderScaling:F3} cellDip={_metrics.CellWidth:F4}x{_metrics.CellHeight:F4} cellPx={_cellWidthDevicePx}x{_cellHeightDevicePx}";
            LogDiagOnce(key, message);
        }

        private static bool ContainsBoxDrawing(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if (cp >= 0x2500 && cp <= 0x257F) return true;
            }
            return false;
        }

        private static bool ContainsRunesRequiringComplexShaping(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if ((cp >= 0x0590 && cp <= 0x0FFF) || // Complex scripts
                    (cp >= 0x1F300 && cp <= 0x1FAFF) || // Emoji
                    IsRegionalIndicatorRune(cp) ||       // Flag emoji sequences
                    (cp >= 0x2600 && cp <= 0x27BF) ||   // Dingbats/symbols
                    (cp == 0x200D))                      // ZWJ
                {
                    return true;
                }
            }
            return false;
        }

        private void DrawDirectGrapheme(
            SKCanvas canvas,
            string grapheme,
            float x,
            float baselineY,
            SKFont glyphFont,
            SKPaint paint,
            float cellX,
            float cellY,
            float cellW,
            float cellH)
        {
            if (!IsSingleRuneInRange(grapheme, 0x2500, 0x257F) || cellW <= 0 || cellH <= 0)
            {
                canvas.DrawText(grapheme, x, baselineY, glyphFont, paint);
                IncrementTextDrawCall();
                IncrementDirectDrawTextCall();
                return;
            }

            // Clip box-drawing glyphs to their exact cell to avoid tiny vertical overlap
            // artifacts when stacked row-by-row with font rendering.
            canvas.Save();
            canvas.ClipRect(SKRect.Create(cellX, cellY, cellW, cellH));

            using var crispFont = new SKFont(glyphFont.Typeface ?? SKTypeface.Default, glyphFont.Size)
            {
                Edging = SKFontEdging.Alias,
                Hinting = SKFontHinting.Full
            };
            canvas.DrawText(grapheme, x, baselineY, crispFont, paint);
            IncrementTextDrawCall();
            IncrementDirectDrawTextCall();
            canvas.Restore();
        }

        private static bool IsBoxDrawingCodePoint(int cp)
            => cp >= 0x2500 && cp <= 0x257F;

        private static bool IsRegionalIndicatorRune(int codePoint)
            => codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF;

        private static bool IsAmbiguousSymbolCodePoint(int codePoint)
            => codePoint >= 0x2600 && codePoint <= 0x27BF;

        private static bool IsEmojiTypefaceFamily(SKTypeface? typeface)
        {
            string family = typeface?.FamilyName ?? string.Empty;
            return family.IndexOf("Emoji", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LogBoxRunDiagnostics(string runText, int runCells, SKFont runFont, float measuredWidth)
        {
            if (!GlyphDiagnosticsEnabled || !ContainsBoxDrawing(runText)) return;

            float expectedWidth = runCells * _metrics.CellWidth;
            string sample = runText.Length <= 24 ? runText : runText.Substring(0, 24);
            string key = $"boxrun:{runFont.Typeface?.FamilyName}:{sample}:{runCells}";
            string msg = $"[GlyphDiag] box-run font='{runFont.Typeface?.FamilyName}' cells={runCells} measuredDip={measuredWidth:F4} expectedDip={expectedWidth:F4} delta={measuredWidth - expectedWidth:F4} sample='{sample}'";
            LogDiagOnce(key, msg);
        }

        private static int FindFirstMissingGlyphCodePoint(string text, SKTypeface primaryTf)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if (cp > 127 && (IsRegionalIndicatorRune(cp) || !primaryTf.ContainsGlyph(cp)))
                {
                    return cp;
                }
            }

            return 0;
        }

        private int GetSafeGraphemeWidth(string textElement)
        {
            if (string.IsNullOrEmpty(textElement)) return 0;
            try
            {
                if (textElement.Length == 1)
                {
                    return Rune.TryCreate(textElement[0], out var rune)
                        ? Math.Max(1, GetRuneWidth(rune))
                        : 1;
                }

                int totalBaseWidth = 0;
                bool hasEmoji = false;
                bool hasZwj = false;
                bool hasModifier = false;
                bool hasEmojiPresentation = false;
                bool hasAmbiguousSymbolBase = false;
                int regionalIndicatorCount = 0;
                int nonRegionalBaseCount = 0;

                foreach (var rune in textElement.EnumerateRunes())
                {
                    int val = rune.Value;
                    if (val == 0x200D) { hasZwj = true; continue; }
                    if (val == 0xFE0F) { hasEmojiPresentation = true; continue; }
                    if (val >= 0x1F3FB && val <= 0x1F3FF) { hasModifier = true; continue; }
                    if (IsCombiningRune(rune)) continue;

                    if (val >= 0x2600 && val <= 0x27BF)
                    {
                        hasAmbiguousSymbolBase = true;
                    }

                    if (IsRegionalIndicatorRune(val))
                    {
                        regionalIndicatorCount++;
                    }
                    else
                    {
                        nonRegionalBaseCount++;
                    }

                    int w = GetRuneWidth(rune);
                    if (totalBaseWidth == 0) totalBaseWidth = w;
                    else if (!hasZwj) totalBaseWidth += w;

                    if (w == 2) hasEmoji = true;
                }

                // Regional indicator symbols combine into 2-cell flag clusters by pair.
                if (regionalIndicatorCount > 0 && nonRegionalBaseCount == 0)
                {
                    int pairs = regionalIndicatorCount / 2;
                    int remainder = regionalIndicatorCount % 2;
                    return (pairs * 2) + (remainder * 2);
                }

                if (hasZwj || hasModifier)
                {
                    return hasEmoji ? 2 : Math.Max(1, totalBaseWidth);
                }

                if (hasEmojiPresentation && hasAmbiguousSymbolBase)
                {
                    return Math.Max(2, totalBaseWidth);
                }

                if (totalBaseWidth == 0) return 0;
                return totalBaseWidth;
            }
            catch (ArgumentOutOfRangeException)
            {
                return 1;
            }
            catch (ArgumentException)
            {
                return 1;
            }
        }

        private static int GetRuneWidth(Rune rune)
        {
            if (IsCombiningRune(rune)) return 0;

            int cp = rune.Value;
            if (cp < 32 || (cp >= 0x7F && cp <= 0x9F)) return 0;
            if (cp >= 0x1100 && cp <= 0x115F) return 2;
            if (cp >= 0x2329 && cp <= 0x232A) return 2;
            if (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) return 2;
            if (cp >= 0xAC00 && cp <= 0xD7A3) return 2;
            if (cp >= 0xF900 && cp <= 0xFAFF) return 2;
            if (cp >= 0xFE10 && cp <= 0xFE6F) return 2;
            if (cp >= 0xFF00 && cp <= 0xFFEF) return 2;
            if (cp >= 0x1F000 && cp <= 0x1FBFF) return 2;
            if (cp >= 0x20000 && cp <= 0x3FFFF) return 2;

            return 1;
        }

        private static bool IsCombiningRune(Rune rune)
        {
            if (rune.Value < 0x80) return false;

            var category = Rune.GetUnicodeCategory(rune);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark ||
                category == System.Globalization.UnicodeCategory.SpacingCombiningMark ||
                category == System.Globalization.UnicodeCategory.EnclosingMark ||
                category == System.Globalization.UnicodeCategory.ModifierSymbol)
            {
                return true;
            }

            int val = rune.Value;
            if (val >= 0x200B && val <= 0x200F) return true;
            if (val >= 0xFE00 && val <= 0xFE0F) return true;
            if (val >= 0x1F3FB && val <= 0x1F3FF) return true;
            if (val >= 0xE0020 && val <= 0xE007F) return true;

            return false;
        }

        private static string NormalizeTextElementForRender(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return " ";
            }

            bool hasInvalidSurrogate = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    {
                        hasInvalidSurrogate = true;
                        break;
                    }
                    i++;
                    continue;
                }

                if (char.IsLowSurrogate(ch))
                {
                    hasInvalidSurrogate = true;
                    break;
                }
            }

            if (!hasInvalidSurrogate)
            {
                return text;
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(ch);
                        sb.Append(text[i + 1]);
                        i++;
                    }
                    else
                    {
                        sb.Append('\uFFFD');
                    }
                    continue;
                }

                if (char.IsLowSurrogate(ch))
                {
                    sb.Append('\uFFFD');
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private SKTypeface ResolveTypefaceForCodePoint(int codePoint, SKTypeface primaryTf)
        {
            if (codePoint <= 127)
            {
                return primaryTf;
            }

            if (IsBoxDrawingCodePoint(codePoint))
            {
                if (!ForceKnownGoodBoxFont && primaryTf.ContainsGlyph(codePoint))
                {
                    return primaryTf;
                }

                string boxLookupKey = $"box:{codePoint}";
                if (_fallbackCache.TryGetValue(boxLookupKey, out var cachedBoxTypeface))
                {
                    return cachedBoxTypeface ?? primaryTf;
                }

                var knownGood = ResolveKnownGoodBoxTypeface(codePoint);
                if (knownGood != null)
                {
                    if (knownGood != primaryTf)
                    {
                        LogDiagOnce(
                            $"box-font-mismatch:{primaryTf.FamilyName}:{knownGood.FamilyName}",
                            $"[GlyphDiag][Warn] box-drawing fallback primary='{primaryTf.FamilyName}' resolved='{knownGood.FamilyName}' codePoint=U+{codePoint:X4}");
                    }
                    _fallbackCache.TryAdd(boxLookupKey, knownGood);
                    return knownGood;
                }

                _fallbackCache.TryAdd(boxLookupKey, primaryTf);
                return primaryTf;
            }

            if (primaryTf.ContainsGlyph(codePoint))
            {
                return primaryTf;
            }

            string lookupKey = codePoint.ToString();
            if (!_fallbackCache.TryGetValue(lookupKey, out var tfToUse))
            {
                SKTypeface? found = null;
                if ((codePoint >= 0x1F300 && codePoint <= 0x1FAFF) ||
                    IsRegionalIndicatorRune(codePoint))
                {
                    found = SKTypeface.FromFamilyName("Segoe UI Emoji");
                }

                if (found == null && IsAmbiguousSymbolCodePoint(codePoint))
                {
                    foreach (var tfChain in _fallbackChain)
                    {
                        if (!IsEmojiTypefaceFamily(tfChain) && tfChain.ContainsGlyph(codePoint))
                        {
                            found = tfChain;
                            break;
                        }
                    }
                }

                if (found == null)
                {
                    foreach (var tfChain in _fallbackChain)
                    {
                        if (tfChain.ContainsGlyph(codePoint))
                        {
                            found = tfChain;
                            break;
                        }
                    }
                }
                found ??= SKFontManager.Default.MatchCharacter(codePoint);
                _fallbackCache.TryAdd(lookupKey, found);
                tfToUse = found;
            }

            return tfToUse ?? primaryTf;
        }

        private SKTypeface? ResolveKnownGoodBoxTypeface(int codePoint)
        {
            foreach (string family in KnownGoodBoxFonts)
            {
                using var candidate = SKTypeface.FromFamilyName(family);
                if (candidate != null && candidate.ContainsGlyph(codePoint))
                {
                    return SKTypeface.FromFamilyName(family);
                }
            }
            return null;
        }

        private static bool TryGetBlockFillRect(
            string grapheme,
            out int xStartEighths,
            out int xEndEighths,
            out int yStartEighths,
            out int yEndEighths)
        {
            xStartEighths = 0;
            xEndEighths = 0;
            yStartEighths = 0;
            yEndEighths = 0;

            var enumerator = grapheme.EnumerateRunes().GetEnumerator();
            if (!enumerator.MoveNext()) return false;
            int cp = enumerator.Current.Value;
            if (enumerator.MoveNext()) return false; // Multi-rune grapheme: not a simple block element.

            // Full block.
            if (cp == 0x2588)
            {
                xStartEighths = 0; xEndEighths = 8;
                yStartEighths = 0; yEndEighths = 8;
                return true;
            }

            // Black square (U+25A0) is used by btop meter bars.
            // Keep full width to avoid seams, but use a centered 4/8 height
            // so visual weight is closer to common terminal glyph rendering.
            if (cp == 0x25A0)
            {
                xStartEighths = 0; xEndEighths = 8;
                yStartEighths = 2; yEndEighths = 6;
                return true;
            }

            // Left-filled block elements (U+2589..U+258F).
            if (cp >= 0x2589 && cp <= 0x258F)
            {
                xStartEighths = 0;
                xEndEighths = 8 - (cp - 0x2588);
                yStartEighths = 0;
                yEndEighths = 8;
                return true;
            }

            // Right half block (U+2590).
            if (cp == 0x2590)
            {
                xStartEighths = 4; xEndEighths = 8;
                yStartEighths = 0; yEndEighths = 8;
                return true;
            }

            // Upper half block (U+2580).
            if (cp == 0x2580)
            {
                xStartEighths = 0; xEndEighths = 8;
                yStartEighths = 0; yEndEighths = 4;
                return true;
            }

            // Lower n/8 block elements (U+2581..U+2587).
            if (cp >= 0x2581 && cp <= 0x2587)
            {
                int n = cp - 0x2580; // 1..7
                xStartEighths = 0; xEndEighths = 8;
                yStartEighths = 8 - n; yEndEighths = 8;
                return true;
            }

            // Upper one eighth block (U+2594).
            if (cp == 0x2594)
            {
                xStartEighths = 0; xEndEighths = 8;
                yStartEighths = 0; yEndEighths = 1;
                return true;
            }

            // Right one eighth block (U+2595).
            if (cp == 0x2595)
            {
                xStartEighths = 7; xEndEighths = 8;
                yStartEighths = 0; yEndEighths = 8;
                return true;
            }

            return false;
        }

        private static bool IsSingleRuneInRange(string grapheme, int minCodePointInclusive, int maxCodePointInclusive)
        {
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;
            return cp >= minCodePointInclusive && cp <= maxCodePointInclusive;
        }

        private static bool ShouldDrawRunDirectWhenGlyphCacheEnabled(string runText)
        {
            bool hasRune = false;
            foreach (var rune in runText.EnumerateRunes())
            {
                hasRune = true;
                // Keep this optimization for whitespace-only runs.
                // Graph/box/braille runs must flow through per-grapheme rendering so
                // primitive and fallback logic can prevent tofu and seam artifacts.
                if (!Rune.IsWhiteSpace(rune))
                {
                    return false;
                }
            }

            return hasRune;
        }

        private static bool ShouldBypassGlyphAtlasForGrapheme(string grapheme)
        {
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;

            if (cp >= 0x2500 && cp <= 0x257F) return true; // Box drawing
            if (cp >= 0x2580 && cp <= 0x259F) return true; // Block elements + shades + quadrants
            if (cp >= 0x2800 && cp <= 0x28FF) return true; // Braille patterns
            if (cp >= 0x25A0 && cp <= 0x25FF) return true; // Geometric block symbols

            return false;
        }

        private static bool ShouldForceDirectTextForGraphGlyph(string grapheme)
        {
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;

            if (cp >= 0x2500 && cp <= 0x257F) return true; // Box drawing
            if (cp >= 0x2580 && cp <= 0x259F) return true; // Block elements + shades + quadrants
            if (cp >= 0x2800 && cp <= 0x28FF) return true; // Braille patterns
            if (cp >= 0x25A0 && cp <= 0x25FF) return true; // Geometric symbols used by TUIs

            return false;
        }

        private static bool TryGetShadeFillAlpha(string grapheme, out float alphaMultiplier)
        {
            alphaMultiplier = 0f;
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;

            alphaMultiplier = cp switch
            {
                0x2591 => 0.28f, // light shade
                0x2592 => 0.50f, // medium shade
                0x2593 => 0.72f, // dark shade
                _ => 0f
            };

            return alphaMultiplier > 0f;
        }

        private static bool TryGetBraillePattern(string grapheme, out byte mask)
        {
            mask = 0;
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;
            if (cp < 0x2800 || cp > 0x28FF) return false;
            mask = (byte)(cp - 0x2800);
            return true;
        }

        private static bool TryGetQuadrantFillMask(string grapheme, out byte mask)
        {
            mask = 0;
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;

            // Bit layout: 1=UL, 2=UR, 4=LL, 8=LR.
            mask = cp switch
            {
                0x2596 => 0b0100, // lower left
                0x2597 => 0b1000, // lower right
                0x2598 => 0b0001, // upper left
                0x2599 => 0b1101, // upper left + lower left + lower right
                0x259A => 0b1001, // upper left + lower right
                0x259B => 0b0111, // upper left + upper right + lower left
                0x259C => 0b1011, // upper left + upper right + lower right
                0x259D => 0b0010, // upper right
                0x259E => 0b0110, // upper right + lower left
                0x259F => 0b1110, // upper right + lower left + lower right
                _ => 0
            };

            return mask != 0;
        }

        private void DrawQuadrantSubcells(SKCanvas canvas, byte mask, float x, float y, float w, float h, SKPaint paint)
        {
            if (mask == 0 || w <= 0 || h <= 0) return;

            float xMid = SnapX(x + (w * 0.5f));
            float yMid = SnapY(y + (h * 0.5f));
            float x2 = x + w;
            float y2 = y + h;

            if ((mask & 0b0001) != 0 && xMid > x && yMid > y) canvas.DrawRect(x, y, xMid - x, yMid - y, paint);       // UL
            if ((mask & 0b0010) != 0 && x2 > xMid && yMid > y) canvas.DrawRect(xMid, y, x2 - xMid, yMid - y, paint);   // UR
            if ((mask & 0b0100) != 0 && xMid > x && y2 > yMid) canvas.DrawRect(x, yMid, xMid - x, y2 - yMid, paint);   // LL
            if ((mask & 0b1000) != 0 && x2 > xMid && y2 > yMid) canvas.DrawRect(xMid, yMid, x2 - xMid, y2 - yMid, paint); // LR
        }

        private bool TryDrawBoxDrawingGlyph(SKCanvas canvas, string grapheme, float x, float y, float w, float h, SKColor color)
        {
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;
            if (cp < 0x2500 || cp > 0x257F) return false;

            const int SegUp = 1 << 0;
            const int SegDown = 1 << 1;
            const int SegLeft = 1 << 2;
            const int SegRight = 1 << 3;

            int seg = 0;
            bool isDouble = false;
            bool isRoundedArc = false;

            switch (cp)
            {
                // Light single-line set used by TUIs
                case 0x2500: seg = SegLeft | SegRight; break; // ─
                case 0x2502: seg = SegUp | SegDown; break; // │
                case 0x250C: seg = SegRight | SegDown; break; // ┌
                case 0x2510: seg = SegLeft | SegDown; break; // ┐
                case 0x2514: seg = SegRight | SegUp; break; // └
                case 0x2518: seg = SegLeft | SegUp; break; // ┘
                case 0x251C: seg = SegUp | SegDown | SegRight; break; // ├
                case 0x2524: seg = SegUp | SegDown | SegLeft; break; // ┤
                case 0x252C: seg = SegLeft | SegRight | SegDown; break; // ┬
                case 0x2534: seg = SegLeft | SegRight | SegUp; break; // ┴
                case 0x253C: seg = SegUp | SegDown | SegLeft | SegRight; break; // ┼

                // Heavy variants rendered as thicker strokes
                case 0x2501: seg = SegLeft | SegRight; break; // ━
                case 0x2503: seg = SegUp | SegDown; break; // ┃
                case 0x250F: seg = SegRight | SegDown; break; // ┏
                case 0x2513: seg = SegLeft | SegDown; break; // ┓
                case 0x2517: seg = SegRight | SegUp; break; // ┗
                case 0x251B: seg = SegLeft | SegUp; break; // ┛
                case 0x2523: seg = SegUp | SegDown | SegRight; break; // ┣
                case 0x252B: seg = SegUp | SegDown | SegLeft; break; // ┫
                case 0x2533: seg = SegLeft | SegRight | SegDown; break; // ┳
                case 0x253B: seg = SegLeft | SegRight | SegUp; break; // ┻
                case 0x254B: seg = SegUp | SegDown | SegLeft | SegRight; break; // ╋

                // Double-line common set
                case 0x2550: seg = SegLeft | SegRight; isDouble = true; break; // ═
                case 0x2551: seg = SegUp | SegDown; isDouble = true; break; // ║
                case 0x2554: seg = SegRight | SegDown; isDouble = true; break; // ╔
                case 0x2557: seg = SegLeft | SegDown; isDouble = true; break; // ╗
                case 0x255A: seg = SegRight | SegUp; isDouble = true; break; // ╚
                case 0x255D: seg = SegLeft | SegUp; isDouble = true; break; // ╝
                case 0x2560: seg = SegUp | SegDown | SegRight; isDouble = true; break; // ╠
                case 0x2563: seg = SegUp | SegDown | SegLeft; isDouble = true; break; // ╣
                case 0x2566: seg = SegLeft | SegRight | SegDown; isDouble = true; break; // ╦
                case 0x2569: seg = SegLeft | SegRight | SegUp; isDouble = true; break; // ╩
                case 0x256C: seg = SegUp | SegDown | SegLeft | SegRight; isDouble = true; break; // ╬

                // Light rounded corners used by TUIs (superfile/lazygit/etc)
                case 0x256D: seg = SegRight | SegDown; isRoundedArc = true; break; // ╭
                case 0x256E: seg = SegLeft | SegDown; isRoundedArc = true; break; // ╮
                case 0x256F: seg = SegLeft | SegUp; isRoundedArc = true; break; // ╯
                case 0x2570: seg = SegRight | SegUp; isRoundedArc = true; break; // ╰

                default:
                    return false;
            }

            int x1px = ToDevicePx(x);
            int y1px = ToDevicePx(y);
            int x2px = ToDevicePx(x + w);
            int y2px = ToDevicePx(y + h);
            if (x2px <= x1px || y2px <= y1px) return false;

            int xMidPx = (x1px + x2px) / 2;
            int yMidPx = (y1px + y2px) / 2;

            int baseThick = Math.Max(1, Math.Min(_cellWidthDevicePx, _cellHeightDevicePx) / 10);
            // Heavy/light both get grid-aligned fill; heavy naturally appears bolder via 2px where possible.
            int thickPx = (cp == 0x2501 || cp == 0x2503 || cp == 0x250F || cp == 0x2513 || cp == 0x2517 || cp == 0x251B || cp == 0x2523 || cp == 0x252B || cp == 0x2533 || cp == 0x253B || cp == 0x254B)
                ? Math.Max(1, baseThick + 1)
                : baseThick;
            int gapPx = Math.Max(1, thickPx + 1);

            using var p = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            };

            static void DrawRectPx(SKCanvas c, SKPaint paint, int px1, int py1, int px2, int py2, Func<int, float> fromPx)
            {
                if (px2 <= px1 || py2 <= py1) return;
                c.DrawRect(fromPx(px1), fromPx(py1), fromPx(px2 - px1), fromPx(py2 - py1), paint);
            }

            void DrawHorizontal(int startPx, int endPx, int centerYPx, int strokePx)
            {
                int top = centerYPx - (strokePx / 2);
                int bottom = top + strokePx;
                DrawRectPx(canvas, p, startPx, top, endPx, bottom, FromDevicePx);
            }

            void DrawVertical(int startPx, int endPx, int centerXPx, int strokePx)
            {
                int left = centerXPx - (strokePx / 2);
                int right = left + strokePx;
                DrawRectPx(canvas, p, left, startPx, right, endPx, FromDevicePx);
            }

            void DrawRoundedCornerStroke(int cornerSeg, int strokePx)
            {
                int halfW = Math.Max(1, (x2px - x1px) / 2);
                int halfH = Math.Max(1, (y2px - y1px) / 2);
                int insetPx = Math.Max(0, strokePx / 2);
                int leftPx = x1px + insetPx;
                int rightPx = x2px - insetPx;
                int topPx = y1px + insetPx;
                int bottomPx = y2px - insetPx;
                if (rightPx <= leftPx || bottomPx <= topPx) return;

                int radiusPx = Math.Max(1, Math.Min(halfW, halfH) - Math.Max(1, strokePx / 2));
                float strokeDip = FromDevicePx(Math.Max(1, strokePx));

                using var roundedPaint = new SKPaint
                {
                    Color = color,
                    Style = SKPaintStyle.Stroke,
                    IsAntialias = false,
                    StrokeWidth = strokeDip,
                    StrokeCap = SKStrokeCap.Butt,
                    StrokeJoin = SKStrokeJoin.Round
                };

                using var path = new SKPath();
                switch (cornerSeg)
                {
                    case SegRight | SegDown: // ╭
                        path.MoveTo(FromDevicePx(rightPx), FromDevicePx(yMidPx));
                        path.LineTo(FromDevicePx(xMidPx + radiusPx), FromDevicePx(yMidPx));
                        path.QuadTo(
                            FromDevicePx(xMidPx + radiusPx), FromDevicePx(yMidPx + radiusPx),
                            FromDevicePx(xMidPx), FromDevicePx(yMidPx + radiusPx));
                        path.LineTo(FromDevicePx(xMidPx), FromDevicePx(bottomPx));
                        break;
                    case SegLeft | SegDown: // ╮
                        path.MoveTo(FromDevicePx(leftPx), FromDevicePx(yMidPx));
                        path.LineTo(FromDevicePx(xMidPx - radiusPx), FromDevicePx(yMidPx));
                        path.QuadTo(
                            FromDevicePx(xMidPx - radiusPx), FromDevicePx(yMidPx + radiusPx),
                            FromDevicePx(xMidPx), FromDevicePx(yMidPx + radiusPx));
                        path.LineTo(FromDevicePx(xMidPx), FromDevicePx(bottomPx));
                        break;
                    case SegLeft | SegUp: // ╯
                        path.MoveTo(FromDevicePx(leftPx), FromDevicePx(yMidPx));
                        path.LineTo(FromDevicePx(xMidPx - radiusPx), FromDevicePx(yMidPx));
                        path.QuadTo(
                            FromDevicePx(xMidPx - radiusPx), FromDevicePx(yMidPx - radiusPx),
                            FromDevicePx(xMidPx), FromDevicePx(yMidPx - radiusPx));
                        path.LineTo(FromDevicePx(xMidPx), FromDevicePx(topPx));
                        break;
                    case SegRight | SegUp: // ╰
                        path.MoveTo(FromDevicePx(rightPx), FromDevicePx(yMidPx));
                        path.LineTo(FromDevicePx(xMidPx + radiusPx), FromDevicePx(yMidPx));
                        path.QuadTo(
                            FromDevicePx(xMidPx + radiusPx), FromDevicePx(yMidPx - radiusPx),
                            FromDevicePx(xMidPx), FromDevicePx(yMidPx - radiusPx));
                        path.LineTo(FromDevicePx(xMidPx), FromDevicePx(topPx));
                        break;
                    default:
                        return;
                }

                canvas.Save();
                canvas.ClipRect(SKRect.Create(FromDevicePx(x1px), FromDevicePx(y1px), FromDevicePx(x2px - x1px), FromDevicePx(y2px - y1px)));
                canvas.DrawPath(path, roundedPaint);
                canvas.Restore();
            }

            if (isRoundedArc)
            {
                DrawRoundedCornerStroke(seg, thickPx);
                return true;
            }

            if (isDouble)
            {
                if ((seg & SegLeft) != 0 || (seg & SegRight) != 0)
                {
                    int yA = yMidPx - gapPx;
                    int yB = yMidPx + gapPx;
                    if ((seg & SegLeft) != 0) { DrawHorizontal(x1px, xMidPx, yA, thickPx); DrawHorizontal(x1px, xMidPx, yB, thickPx); }
                    if ((seg & SegRight) != 0) { DrawHorizontal(xMidPx, x2px, yA, thickPx); DrawHorizontal(xMidPx, x2px, yB, thickPx); }
                }

                if ((seg & SegUp) != 0 || (seg & SegDown) != 0)
                {
                    int xA = xMidPx - gapPx;
                    int xB = xMidPx + gapPx;
                    if ((seg & SegUp) != 0) { DrawVertical(y1px, yMidPx, xA, thickPx); DrawVertical(y1px, yMidPx, xB, thickPx); }
                    if ((seg & SegDown) != 0) { DrawVertical(yMidPx, y2px, xA, thickPx); DrawVertical(yMidPx, y2px, xB, thickPx); }
                }
            }
            else
            {
                if ((seg & SegLeft) != 0) DrawHorizontal(x1px, xMidPx, yMidPx, thickPx);
                if ((seg & SegRight) != 0) DrawHorizontal(xMidPx, x2px, yMidPx, thickPx);
                if ((seg & SegUp) != 0) DrawVertical(y1px, yMidPx, xMidPx, thickPx);
                if ((seg & SegDown) != 0) DrawVertical(yMidPx, y2px, xMidPx, thickPx);

                bool hasH = (seg & (SegLeft | SegRight)) != 0;
                bool hasV = (seg & (SegUp | SegDown)) != 0;
                if (hasH && hasV)
                {
                    int cx1 = xMidPx - (thickPx / 2);
                    int cy1 = yMidPx - (thickPx / 2);
                    DrawRectPx(canvas, p, cx1, cy1, cx1 + thickPx, cy1 + thickPx, FromDevicePx);
                }
            }

            return true;
        }

        private void DrawBrailleSubcells(SKCanvas canvas, byte mask, float x, float y, float w, float h, SKPaint paint)
        {
            if (mask == 0 || w <= 0 || h <= 0) return;

            float subW = w * 0.5f;
            float subH = h * 0.25f;
            float dotW = subW * 0.56f;
            float dotH = subH * 0.56f;

            // Braille bit mapping: 1/2/3/7 on left column, 4/5/6/8 on right column.
            DrawBrailleDotInSubcell(canvas, mask, bit: 0, col: 0, row: 0, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 1, col: 0, row: 1, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 2, col: 0, row: 2, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 3, col: 1, row: 0, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 4, col: 1, row: 1, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 5, col: 1, row: 2, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 6, col: 0, row: 3, x, y, subW, subH, dotW, dotH, paint);
            DrawBrailleDotInSubcell(canvas, mask, bit: 7, col: 1, row: 3, x, y, subW, subH, dotW, dotH, paint);
        }

        private void DrawBrailleDotInSubcell(
            SKCanvas canvas,
            byte mask,
            int bit,
            int col,
            int row,
            float x,
            float y,
            float subW,
            float subH,
            float dotW,
            float dotH,
            SKPaint paint)
        {
            if ((mask & (1 << bit)) == 0) return;

            float cx = x + ((col + 0.5f) * subW);
            float cy = y + ((row + 0.5f) * subH);
            float x1 = SnapX(cx - (dotW * 0.5f));
            float x2 = SnapX(cx + (dotW * 0.5f));
            float y1 = SnapY(cy - (dotH * 0.5f));
            float y2 = SnapY(cy + (dotH * 0.5f));
            if (x2 > x1 && y2 > y1)
            {
                canvas.DrawRect(x1, y1, x2 - x1, y2 - y1, paint);
            }
        }

        private static bool TryGetSingleRuneCodePoint(string grapheme, out int codePoint)
        {
            codePoint = 0;
            var enumerator = grapheme.EnumerateRunes().GetEnumerator();
            if (!enumerator.MoveNext()) return false;
            codePoint = enumerator.Current.Value;
            return !enumerator.MoveNext();
        }

        private static bool ContainsNonWhitespace(string text)
        {
            foreach (char ch in text)
            {
                if (!char.IsWhiteSpace(ch)) return true;
            }

            return false;
        }

        private sealed class SKTypefaceReferenceComparer : IEqualityComparer<SKTypeface>
        {
            public static readonly SKTypefaceReferenceComparer Instance = new();

            public bool Equals(SKTypeface? x, SKTypeface? y)
                => ReferenceEquals(x, y);

            public int GetHashCode(SKTypeface obj)
                => RuntimeHelpers.GetHashCode(obj);
        }

        private bool TryGetUnderlineBounds(string runText, int runStartCol, float[] colEdges, float paddingLeft, out float x1, out float x2)
        {
            x1 = 0f;
            x2 = 0f;

            int cellOffset = 0;
            int startCellOffset = -1;
            int endCellOffsetExclusive = -1;

            foreach (var rune in runText.EnumerateRunes())
            {
                string grapheme = rune.ToString();
                int w = GetSafeGraphemeWidth(grapheme);
                if (w <= 0) continue;

                if (ContainsNonWhitespace(grapheme))
                {
                    if (startCellOffset < 0) startCellOffset = cellOffset;
                    endCellOffsetExclusive = cellOffset + w;
                }

                cellOffset += w;
            }

            if (startCellOffset < 0 || endCellOffsetExclusive <= startCellOffset)
            {
                return false;
            }

            x1 = GetColEdge(colEdges, runStartCol + startCellOffset, paddingLeft);
            x2 = GetColEdge(colEdges, runStartCol + endCellOffsetExclusive, paddingLeft);
            return x2 > x1;
        }

        private static SKColor BlendTowards(SKColor source, SKColor target, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            byte r = (byte)(source.Red + ((target.Red - source.Red) * t));
            byte g = (byte)(source.Green + ((target.Green - source.Green) * t));
            byte b = (byte)(source.Blue + ((target.Blue - source.Blue) * t));
            return new SKColor(r, g, b, source.Alpha);
        }

        private static SKColor EnsureReadableForeground(SKColor fg, SKColor bg, SKColor fallback)
        {
            if (fg.Red != bg.Red || fg.Green != bg.Green || fg.Blue != bg.Blue) return fg;

            // If fg collides with bg, swap to theme fallback or its inverse contrast.
            if (fallback.Red != bg.Red || fallback.Green != bg.Green || fallback.Blue != bg.Blue)
            {
                return new SKColor(fallback.Red, fallback.Green, fallback.Blue, fg.Alpha);
            }

            int luminance = (299 * bg.Red + 587 * bg.Green + 114 * bg.Blue) / 1000;
            byte v = luminance > 127 ? (byte)0 : (byte)255;
            return new SKColor(v, v, v, fg.Alpha);
        }

        private SKColor ResolveCellForeground(RenderCellSnapshot cell, SKColor themeFg, byte alpha, RenderThemeSnapshot themeSnapshot)
        {
            if (cell.IsDefaultForeground) return themeFg;
            if (cell.FgIndex >= 0)
            {
                var c = ResolvePaletteIndex(cell.FgIndex, themeSnapshot);
                return new SKColor(c.R, c.G, c.B, 255);
            }
            return new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, 255);
        }

        private SKColor ResolveCellBackground(RenderCellSnapshot cell, SKColor themeBg, byte alpha, RenderThemeSnapshot themeSnapshot)
        {
            if (cell.IsDefaultBackground) return themeBg;
            if (cell.BgIndex >= 0)
            {
                var c = ResolvePaletteIndex(cell.BgIndex, themeSnapshot);
                return new SKColor(c.R, c.G, c.B, 255);
            }
            return new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, 255);
        }

        private static TermColor ResolvePaletteIndex(int index, RenderThemeSnapshot themeSnapshot)
        {
            // 0-15 come from the active theme palette.
            if (index < 16)
            {
                if (themeSnapshot.AnsiPalette != null && (uint)index < (uint)themeSnapshot.AnsiPalette.Length)
                {
                    return themeSnapshot.AnsiPalette[index];
                }

                return TermColor.White;
            }

            // 16-231: xterm 6x6x6 cube.
            if (index < 232)
            {
                index -= 16;
                int r = index / 36;
                int g = (index / 6) % 6;
                int b = index % 6;

                static byte ToByte(int v) => (byte)(v == 0 ? 0 : (v * 40 + 55));
                return TermColor.FromRgb(ToByte(r), ToByte(g), ToByte(b));
            }

            // 232-255: xterm grayscale ramp.
            if (index < 256)
            {
                index -= 232;
                byte v = (byte)(index * 10 + 8);
                return TermColor.FromRgb(v, v, v);
            }

            return TermColor.White;
        }
    }
}
