// TerminalDrawOperation.cs (drop-in replacement)
// Notes:
// - Preserves existing constructor signature used by TerminalView.Render(...)
// - Fixes baselineY usage + rowTopY background placement
// - Removes fragile "recording canvas" type checks (explicit drawBackgrounds flag)
// - Fixes active match detection WITHOUT IndexOf (avoids O(n^2))
// - Reuses StringBuilder per row to reduce GC churn
// - Keeps Z-order: Text -> Images -> Overlays -> Cursor
// - Keeps lock timing instrumentation as-is (Step 4 will shrink lock scope)
// - Root cause addressed: fractional cell metrics + fallback font advances caused deterministic
//   box-drawing drift. We now render on a snapped device-pixel cell grid and log fallback/advance
//   diagnostics behind env flags.

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
using System.Text;

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
        private readonly int _totalLines;
        private readonly int _cursorRow;
        private readonly int _cursorCol;
        private readonly RowImageCache? _rowCache;
        private readonly bool _enableComplexShaping;
        private readonly GlyphCache? _glyphCache;
        private readonly int _cellWidthDevicePx;
        private readonly int _cellHeightDevicePx;

        private static readonly bool GlyphDiagnosticsEnabled = IsEnvFlagEnabled("NOVATERM_DIAG_GLYPH");
        private static readonly bool GridDiagnosticsEnabled = IsEnvFlagEnabled("NOVATERM_DIAG_GRID");
        private static readonly bool ForceKnownGoodBoxFont = IsEnvFlagEnabled("NOVATERM_FORCE_BOX_FONT");
        private static readonly bool UseBoxDrawingPrimitives = IsEnvFlagEnabled("NOVATERM_BOX_PRIMITIVES");
        private static readonly bool UseBlockElementPrimitives = IsEnvFlagEnabled("NOVATERM_BLOCK_PRIMITIVES");
        private static readonly ConcurrentDictionary<string, byte> GlyphDiagOnce = new();
        private static readonly string[] KnownGoodBoxFonts = { "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", "Cascadia Code" };
        private static readonly Lazy<RenderPerfWriter?> SharedRenderPerfWriter = new(RenderPerfWriter.CreateFromEnvironment);

        // Batching for DrawAtlas
        private readonly List<SKRect> _alphaRects = new();
        private readonly List<SKRotationScaleMatrix> _alphaXforms = new();
        private readonly List<SKColor> _alphaColors = new();
        private readonly List<SKRect> _colorRects = new();
        private readonly List<SKRotationScaleMatrix> _colorXforms = new();
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
            GlyphCache? glyphCache = null)
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
            _totalLines = totalLines;
            _cursorRow = cursorRow;
            _cursorCol = cursorCol;
            _rowCache = rowCache;
            _enableComplexShaping = enableComplexShaping;
            _glyphCache = glyphCache;
            _cellWidthDevicePx = Math.Max(1, ToDevicePx(_metrics.CellWidth));
            _cellHeightDevicePx = Math.Max(1, ToDevicePx(_metrics.CellHeight));
        }

        public void Dispose()
        {
            _skFont?.Dispose();
            _skTypeface?.Dispose();

            _alphaRects.Clear();
            _alphaXforms.Clear();
            _alphaColors.Clear();
            _colorRects.Clear();
            _colorXforms.Clear();
            ReturnEdgeBuffers();
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
            DrawTerminal(canvas);
            canvas.Restore();
        }

        // Step 3 optimization: rely on row pictures (or DrawRowText fallback) to paint non-default backgrounds.
        private const bool UseRowPicturesForBackgrounds = true;

        // Step 5: Always build SKPictures for dirty rows — skipping caching causes stale
        // cache hits on subsequent frames (mass invalidation "ghost" bug).
        private const int MaxPictureBuildsPerFrame = int.MaxValue;
        private const float MassInvalidationThreshold = 0.5f; // kept for stats only

        // Drop-in replacement for TerminalDrawOperation.DrawTerminal(SKCanvas canvas)
        // Fixes:
        // 1) DrainDisposalsAndApplyClearIfRequested() is called OUTSIDE _buffer lock
        // 2) Preserves row->Y mapping even if some rows are missing (uses VisualRow)
        // 3) Uses a single snapshotted absDisplayStart for consistent image/overlay/cursor mapping

        private void DrawTerminal(SKCanvas canvas)
        {
            try
            {
                // Render-thread safe boundary: drain deferred disposals & apply clear requests.
                _rowCache?.DrainDisposalsAndApplyClearIfRequested();
                _glyphCache?.DrainDisposals();

                RenderPerfWriter? perfWriter = BeginFramePerfMetrics();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var frame = new FrameSnapshot();

                int bufferRows = _bufferRows;
                int bufferCols = _bufferCols;
                int absDisplayStart = 0;
                byte alpha = (byte)(255 * _opacity);
                int dirtyRowCount = 0;
                bool isMassInvalidation = false;
                var frameSw = System.Diagnostics.Stopwatch.StartNew();

                // --------------------
                // Snapshot Phase (LOCK HELD)
                // --------------------
                _buffer.Lock.EnterReadLock();
                try
                {
                    frame.ThemeBg = new SKColor(_buffer.Theme.Background.R, _buffer.Theme.Background.G, _buffer.Theme.Background.B, alpha);
                    frame.ThemeFg = new SKColor(_buffer.Theme.Foreground.R, _buffer.Theme.Foreground.G, _buffer.Theme.Foreground.B, 255);
                    frame.CursorColor = new SKColor(_buffer.Theme.CursorColor.R, _buffer.Theme.CursorColor.G, _buffer.Theme.CursorColor.B, 255);
                    frame.CursorStyle = _buffer.Modes.CursorStyle;
                    // Intentionally leave ThemeBg with its window opacity to paint the base layer

                    absDisplayStart = Math.Max(0, _totalLines - _bufferRows - _scrollOffset);

                    // IMPORTANT: Always add exactly one RowRenderItem per visual row
                    // so render loop can safely use r for Y positioning.
                    for (int r = 0; r < bufferRows; r++)
                    {
                        int absRow = absDisplayStart + r;

                        var item = new RowRenderItem
                        {
                            AbsRow = absRow,
                            CachedPicture = null,
                            Snapshot = null
                        };

                        var row = _buffer.GetRowAbsolute(absRow);
                        if (row != null)
                        {
                            // Cache probe using unique RowId
                            item.CachedPicture = _rowCache?.Get(row.Id, row.Revision);

                            if (item.CachedPicture != null)
                            {
                                RendererStatistics.RecordRowCacheHit();
                                IncrementRowPictureCacheHit();
                            }
                            else
                            {
                                RendererStatistics.RecordRowCacheMiss();
                                IncrementRowPictureCacheMiss();
                                item.Snapshot = _buffer.GetRowSnapshot(absRow, bufferCols);
                                RendererStatistics.RecordRowSnapshot();
                                dirtyRowCount++;
                            }
                        }
                        // else: row missing -> leave item empty; base background will show

                        frame.RowItems.Add(item);
                    }

                    isMassInvalidation = (bufferRows > 0) && ((float)dirtyRowCount / bufferRows > MassInvalidationThreshold);
                    frame.Images = _buffer.GetVisibleImagesSnapshot(absDisplayStart, bufferRows);


                }
                finally
                {
                    _buffer.Lock.ExitReadLock();
                    sw.Stop();
                    RendererStatistics.RecordReadLockTime(sw.ElapsedMilliseconds);
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

                float paddingLeft = 4f;
                float paddingTop = 0f;
                float[] colEdges = EnsureCellEdgeGrid(ref _colEdges, ref _colEdgesCount, bufferCols, paddingLeft, _cellWidthDevicePx);
                float[] rowEdges = EnsureCellEdgeGrid(ref _rowEdges, ref _rowEdgesCount, bufferRows, paddingTop, _cellHeightDevicePx);

                int dirtyCells = 0;
                int buildsThisFrame = 0;
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

                    float rowTopY = GetRowEdge(rowEdges, r, paddingTop);
                    float baselineY = SnapY(rowTopY + _metrics.Baseline);

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

                        if (buildsThisFrame < maxBuilds)
                        {
                            var recSw = System.Diagnostics.Stopwatch.StartNew();
                            using var recorder = new SKPictureRecorder();
                            using var rowCanvas = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, (float)_metrics.CellHeight));

                            // Row-local coordinates for cached picture recording
                            DrawRowTextFromSnapshot(
                                rowCanvas,
                                snapshot,
                                rowTopY: 0f,
                                baselineY: SnapY(_metrics.Baseline),
                                colEdges: colEdges,
                                font,
                                fgPaint,
                                frame.ThemeFg,
                                frame.ThemeBg,
                                alpha,
                                tf,
                                drawBackgrounds: true);

                            var picture = recorder.EndRecording();
                            _rowCache?.Add(snapshot.RowId, snapshot.Revision, picture);
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
                                alpha,
                                tf,
                                drawBackgrounds: true);
                        }

                        dirtyCells += bufferCols;
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
                int matchIndex = 0;
                for (int r = 0; r < bufferRows; r++)
                {
                    float y = GetRowEdge(rowEdges, r, paddingTop);
                    float rowBottom = GetRowEdge(rowEdges, r + 1, paddingTop);
                    float rowHeight = rowBottom - y;
                    int absRow = absDisplayStart + r;

                    if (_selection.IsActive)
                    {
                        var (isSelected, colStart, colEnd) = _selection.GetSelectionRangeForRow(absRow, bufferCols);
                        if (isSelected)
                        {
                            float x1 = GetColEdge(colEdges, colStart, paddingLeft);
                            float x2 = GetColEdge(colEdges, colEnd + 1, paddingLeft);
                            canvas.DrawRect(x1, y, x2 - x1, rowHeight, selectionPaint);
                            IncrementRectDrawCall();
                        }
                    }

                    // Sub-linear scan for search matches (assumes _searchMatches are sorted by AbsRow)
                    if (_searchMatches != null && _searchMatches.Count > 0)
                    {
                        // Skip matches BEFORE current row
                        while (matchIndex < _searchMatches.Count && _searchMatches[matchIndex].AbsRow < absRow)
                            matchIndex++;

                        // Render matches ON current row
                        int tempIdx = matchIndex;
                        while (tempIdx < _searchMatches.Count && _searchMatches[tempIdx].AbsRow == absRow)
                        {
                            var m = _searchMatches[tempIdx];
                            var p = (tempIdx == _activeSearchIndex) ? activeMatchPaint : matchPaint;
                            float x1 = GetColEdge(colEdges, m.StartCol, paddingLeft);
                            float x2 = GetColEdge(colEdges, m.EndCol + 1, paddingLeft);
                            canvas.DrawRect(x1, y, x2 - x1, rowHeight, p);
                            IncrementRectDrawCall();
                            tempIdx++;
                        }
                    }
                }

                // Cursor (optional policy: hide cursor when scrolled back)
                if (!_hideCursor && _scrollOffset == 0)
                {
                    int absCursorRow = (_totalLines - bufferRows) + _cursorRow;
                    int visualRow = absCursorRow - absDisplayStart;

                    if (visualRow >= 0 && visualRow < bufferRows)
                    {
                        float x1 = GetColEdge(colEdges, _cursorCol, paddingLeft);
                        float x2 = GetColEdge(colEdges, _cursorCol + 1, paddingLeft);
                        float rowTop = GetRowEdge(rowEdges, visualRow, paddingTop);
                        float rowBottom = GetRowEdge(rowEdges, visualRow + 1, paddingTop);
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
                                    float beamW = Math.Max(1f, (float)Math.Floor(_metrics.CellWidth * 0.14));
                                    canvas.DrawRect(x1, rowTop, beamW, rowHeight, cursorPaint);
                                    IncrementRectDrawCall();
                                    break;
                                }
                            case CursorStyle.Underline:
                            default:
                                {
                                    float uY = SnapY(rowTop + rowHeight - 2);
                                    canvas.DrawRect(x1, uY, x2 - x1, 2, cursorPaint);
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
                CompleteFramePerfMetrics(perfWriter, frameSw.Elapsed.TotalMilliseconds, dirtyRowCount);
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("error.log", "\n--- Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
                throw;
            }
        }

        private void FlushBatches(SKCanvas canvas)
        {
            if (_glyphCache == null) return;

            var (alphaAtlas, colorAtlas) = _glyphCache.GetAtlasImages();
            int alphaGlyphs = _alphaRects.Count;
            int colorGlyphs = _colorRects.Count;
            int atlasDrawCalls = 0;

            if (alphaGlyphs > 0)
            {
                using var paint = new SKPaint { IsAntialias = false };
                canvas.DrawAtlas(
                    alphaAtlas,
                    _alphaRects.ToArray(),
                    _alphaXforms.ToArray(),
                    _alphaColors.ToArray(),
                    SKBlendMode.Modulate,
                    new SKSamplingOptions(SKFilterMode.Nearest),
                    paint);
                atlasDrawCalls++;

                _alphaRects.Clear();
                _alphaXforms.Clear();
                _alphaColors.Clear();
            }

            if (colorGlyphs > 0)
            {
                using var paint = new SKPaint { IsAntialias = false };
                canvas.DrawAtlas(
                    colorAtlas,
                    _colorRects.ToArray(),
                    _colorXforms.ToArray(),
                    null,
                    SKBlendMode.SrcOver,
                    new SKSamplingOptions(SKFilterMode.Linear),
                    paint);
                atlasDrawCalls++;

                _colorRects.Clear();
                _colorXforms.Clear();
            }

            if (atlasDrawCalls > 0)
            {
                RecordFlush(alphaGlyphs, colorGlyphs, atlasDrawCalls);
            }
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
            byte alpha,
            SKTypeface primaryTf,
            bool drawBackgrounds)
        {
            const float paddingLeft = 4f;
            int cellsRendered = 0;
            float snappedCellHeight = FromDevicePx(_cellHeightDevicePx);

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

                var cb = ResolveCellBackground(cell, themeBg, alpha);
                var cf = ResolveCellForeground(cell, themeFg, alpha);
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

                    var ncb = ResolveCellBackground(next, themeBg, alpha);
                    var ncf = ResolveCellForeground(next, themeFg, alpha);
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
                    }

                    runHasBoxDrawing |= cellHasBoxDrawing;
                    runHasComplexShapingGlyph |= cellNeedsComplexShaping;
                    runNeedsComplexShaping = runHasComplexShapingGlyph && !runHasBoxDrawing;

                    runBuilder.Append(cellText);
                    int w = GetSafeGraphemeWidth(cellText); // GetGraphemeWidth is thread-safe
                    totalRunWidth += w;
                    k++;
                }

                string runText = runBuilder.ToString();
                float rx = GetColEdge(colEdges, c, paddingLeft);
                float rx2 = GetColEdge(colEdges, c + totalRunWidth, paddingLeft);
                fgPaint.Color = fg;
                float rw = rx2 - rx;
                float strokeWidth = Math.Max(1f, (float)(_metrics.CellHeight * 0.06));

                // Backgrounds (when requested)
                if (drawBackgrounds && bg != themeBg && bg.Alpha != 0)
                {
                    using var bgP = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(rx, rowTopY, rw, snappedCellHeight, bgP);
                    IncrementRectDrawCall();
                }

                bool appliedItalicTransform = false;
                if (runIsItalic)
                {
                    FlushBatches(canvas);
                    canvas.Save();
                    canvas.Translate(rx, baselineY);
                    canvas.Skew(-0.22f, 0f);
                    canvas.Translate(-rx, -baselineY);
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
                        canvas.SaveLayer(new SKRect(rx, rowTopY, rx + rw, rowTopY + (float)_metrics.CellHeight), null);
                    }

                    using var fFont = new SKFont(tfToUse ?? primaryTf, (float)_fontSize) { Edging = SKFontEdging.Antialias };
                    using var shaper = new SKShaper(tfToUse ?? primaryTf);
                    LogBoxRunDiagnostics(runText, totalRunWidth, fFont, fFont.MeasureText(runText));
                    canvas.DrawShapedText(shaper, runText, rx, baselineY, fFont, fgPaint);
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
                            canvas.DrawText(runText, rx, baselineY, font, fgPaint);
                            IncrementTextDrawCall();
                            IncrementDirectDrawTextCall();
                            cellsRendered += totalRunWidth;
                            c = k - 1;
                            continue;
                        }

                        int cellX = c;
                        float yBaselineSnap = SnapY(baselineY);
                        float glyphY = SnapY(yBaselineSnap + font.Metrics.Ascent);
                        float invScale = (float)(1.0 / _renderScaling);
                        using var blockPaint = new SKPaint
                        {
                            Color = fg,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = false
                        };

                        foreach (var rune in runText.EnumerateRunes())
                        {
                            string grapheme = rune.ToString();
                            int graphemeWidth = GetSafeGraphemeWidth(grapheme);
                            float xIdxSnap = GetColEdge(colEdges, cellX, paddingLeft);

                            int fallbackChar = FindFirstMissingGlyphCodePoint(grapheme, primaryTf);
                            SKFont glyphFont = font;
                            SKFont? fallbackFont = null;
                            if (fallbackChar != 0)
                            {
                                var glyphTf = ResolveTypefaceForCodePoint(fallbackChar, primaryTf);
                                if (glyphTf != primaryTf)
                                {
                                    fallbackFont = new SKFont(glyphTf, (float)_fontSize) { Edging = SKFontEdging.Antialias };
                                    glyphFont = fallbackFont;
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
                            float cellX2 = GetColEdge(colEdges, cellX + graphemeWidth, paddingLeft);
                            float cellW = cellX2 - cellX1;
                            float cellH = snappedCellHeight;

                            // Render block/braille primitives only when explicitly enabled.
                            // Default path keeps font-authored glyph rasterization, which is required
                            // for image-as-text previews (e.g. superfile/chafa) to avoid artifacting.
                            if (UseBlockElementPrimitives &&
                                TryGetBlockFillRect(grapheme, out int xStartEighths, out int xEndEighths, out int yStartEighths, out int yEndEighths))
                            {
                                FlushBatches(canvas);

                                float fillX1 = xStartEighths == 0 ? cellX1 : SnapX(cellX1 + (cellW * (xStartEighths / 8f)));
                                float fillX2 = xEndEighths == 8 ? cellX2 : SnapX(cellX1 + (cellW * (xEndEighths / 8f)));
                                float rowBottom = rowTopY + cellH;
                                float fillY1 = yStartEighths == 0 ? rowTopY : SnapY(rowTopY + (cellH * (yStartEighths / 8f)));
                                float fillY2 = yEndEighths == 8 ? rowBottom : SnapY(rowTopY + (cellH * (yEndEighths / 8f)));
                                if (fillX2 > fillX1 && fillY2 > fillY1)
                                {
                                    canvas.DrawRect(fillX1, fillY1, fillX2 - fillX1, fillY2 - fillY1, blockPaint);
                                    IncrementRectDrawCall();
                                }

                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (UseBlockElementPrimitives && TryGetShadeFillAlpha(grapheme, out float shadeAlpha))
                            {
                                FlushBatches(canvas);
                                byte shadeA = (byte)Math.Clamp((int)Math.Round(fg.Alpha * shadeAlpha), 0, 255);
                                using var shadePaint = new SKPaint
                                {
                                    Color = new SKColor(fg.Red, fg.Green, fg.Blue, shadeA),
                                    Style = SKPaintStyle.Fill,
                                    IsAntialias = false
                                };
                                if (cellW > 0 && cellH > 0)
                                {
                                    canvas.DrawRect(cellX1, rowTopY, cellW, cellH, shadePaint);
                                    IncrementRectDrawCall();
                                }

                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (UseBlockElementPrimitives && TryGetQuadrantFillMask(grapheme, out byte quadrantMask))
                            {
                                FlushBatches(canvas);
                                DrawQuadrantSubcells(canvas, quadrantMask, cellX1, rowTopY, cellW, cellH, blockPaint);
                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (UseBlockElementPrimitives && TryGetBraillePattern(grapheme, out byte brailleMask))
                            {
                                FlushBatches(canvas);
                                DrawBrailleSubcells(canvas, brailleMask, cellX1, rowTopY, cellW, cellH, blockPaint);
                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (UseBoxDrawingPrimitives &&
                                TryGetSingleRuneCodePoint(grapheme, out int cpBox) &&
                                cpBox >= 0x2500 && cpBox <= 0x257F)
                            {
                                FlushBatches(canvas);
                                if (TryDrawBoxDrawingGlyph(canvas, grapheme, cellX1, rowTopY, cellW, cellH, fg))
                                {
                                    fallbackFont?.Dispose();
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
                                    rowTopY,
                                    cellW,
                                    cellH);
                                fallbackFont?.Dispose();
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
                                    rowTopY,
                                    cellW,
                                    cellH);
                                fallbackFont?.Dispose();
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
                                    _alphaRects.Add(rect);
                                    _alphaXforms.Add(xform);
                                    _alphaColors.Add(fg);
                                }
                                else
                                {
                                    _colorRects.Add(rect);
                                    _colorXforms.Add(xform);
                                }
                            }
                            else
                            {
                                FlushBatches(canvas);
                                canvas.DrawText(grapheme, xIdxSnap, yBaselineSnap, glyphFont, fgPaint);
                                IncrementTextDrawCall();
                                IncrementDirectDrawTextCall();
                            }
                            fallbackFont?.Dispose();
                            cellX += graphemeWidth;
                        }
                        LogBoxRunDiagnostics(runText, totalRunWidth, font, font.MeasureText(runText));
                    }
                    else
                    {
                        canvas.DrawText(runText, rx, baselineY, font, fgPaint);
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
                    using var decoPaint = new SKPaint
                    {
                        Color = fg,
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = strokeWidth
                    };

                    if (underlineHasVisibleText)
                    {
                        float underlineX1 = rx;
                        float underlineX2 = rx + rw;
                        if (TryGetUnderlineBounds(runText, c, colEdges, paddingLeft, out float trimmedX1, out float trimmedX2))
                        {
                            underlineX1 = trimmedX1;
                            underlineX2 = trimmedX2;
                        }

                        float underlineY = SnapY(rowTopY + _metrics.CellHeight - Math.Max(1.5, _metrics.CellHeight * 0.12));
                        underlineY = Math.Min(underlineY, rowTopY + snappedCellHeight);
                        canvas.DrawLine(underlineX1, underlineY, underlineX2, underlineY, decoPaint);
                        IncrementRectDrawCall();
                    }

                    if (runIsStrikethrough)
                    {
                        float strikeY = SnapY(rowTopY + (snappedCellHeight * 0.52));
                        canvas.DrawLine(rx, strikeY, rx + rw, strikeY, decoPaint);
                        IncrementRectDrawCall();
                    }
                }

                c = k - 1;
            }

            FlushBatches(canvas);
            return cellsRendered;
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

        private void CompleteFramePerfMetrics(RenderPerfWriter? writer, double frameTimeMs, int dirtyRows)
        {
            if (!_collectFramePerfMetrics || writer == null)
            {
                return;
            }

            _framePerfMetrics.FrameTimeMs = frameTimeMs;
            _framePerfMetrics.DirtyRows = dirtyRows;
            _framePerfMetrics.DirtySpansTotal = dirtyRows;

            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - _frameAllocStartBytes;
            _framePerfMetrics.AllocBytesThisFrame = allocDelta > 0 ? allocDelta : 0;
            _framePerfMetrics.DrawCallsTotal = _framePerfMetrics.DrawCallsText + _framePerfMetrics.DrawCallsRects + _frameOtherDrawCalls;
            writer.TryWrite(_framePerfMetrics);
        }

        private void IncrementRowPictureCacheHit()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.RowPictureCacheHits++;
            }
        }

        private void IncrementRowPictureCacheMiss()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.RowPictureCacheMisses++;
            }
        }

        private void IncrementPictureBuild()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.PictureBuilds++;
            }
        }

        private void IncrementRectDrawCall()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.DrawCallsRects++;
            }
        }

        private void IncrementTextDrawCall()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.DrawCallsText++;
            }
        }

        private void IncrementDirectDrawTextCall()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.DirectDrawTextCount++;
            }
        }

        private void IncrementShapedTextRun()
        {
            if (_collectFramePerfMetrics)
            {
                _framePerfMetrics.ShapedTextRuns++;
            }
        }

        private void IncrementOtherDrawCall()
        {
            if (_collectFramePerfMetrics)
            {
                _frameOtherDrawCalls++;
            }
        }

        private void RecordFlush(int alphaGlyphs, int colorGlyphs, int atlasDrawCalls)
        {
            if (!_collectFramePerfMetrics)
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

        private float[] EnsureCellEdgeGrid(ref float[]? buffer, ref int usedCount, int cellCount, float logicalStart, int deviceCellSize)
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
            int startPx = ToDevicePx(logicalStart);
            for (int i = 0; i <= count; i++)
            {
                buffer[i] = FromDevicePx(startPx + (i * deviceCellSize));
            }
            return buffer;
        }

        private float GetColEdge(float[] colEdges, int colIndex, float paddingLeft)
        {
            if ((uint)colIndex < (uint)colEdges.Length) return colEdges[colIndex];
            return SnapX((colIndex * _metrics.CellWidth) + paddingLeft);
        }

        private float GetRowEdge(float[] rowEdges, int rowIndex, float paddingTop)
        {
            if ((uint)rowIndex < (uint)rowEdges.Length) return rowEdges[rowIndex];
            return SnapY((rowIndex * _metrics.CellHeight) + paddingTop);
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
                if (cp > 127 && !primaryTf.ContainsGlyph(cp))
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
                return Math.Max(1, _buffer.GetGraphemeWidth(textElement));
            }
            catch (ArgumentOutOfRangeException)
            {
                return 1;
            }
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
                    (codePoint >= 0x2600 && codePoint <= 0x27BF))
                {
                    found = SKTypeface.FromFamilyName("Segoe UI Emoji");
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
                int cp = rune.Value;
                if (Rune.IsWhiteSpace(rune))
                {
                    continue;
                }
                if ((cp >= 0x2580 && cp <= 0x259F) || // Block elements, shades, quadrants
                    (cp >= 0x2800 && cp <= 0x28FF) || // Braille patterns
                    (cp >= 0x25A0 && cp <= 0x25FF))   // Geometric symbols used by TUIs
                {
                    continue;
                }

                return false;
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

        private SKColor ResolveCellForeground(RenderCellSnapshot cell, SKColor themeFg, byte alpha)
        {
            if (cell.IsDefaultForeground) return themeFg;
            if (cell.FgIndex >= 0)
            {
                var c = ResolvePaletteIndex(cell.FgIndex);
                return new SKColor(c.R, c.G, c.B, 255);
            }
            return new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, 255);
        }

        private SKColor ResolveCellBackground(RenderCellSnapshot cell, SKColor themeBg, byte alpha)
        {
            if (cell.IsDefaultBackground) return themeBg;
            if (cell.BgIndex >= 0)
            {
                var c = ResolvePaletteIndex(cell.BgIndex);
                return new SKColor(c.R, c.G, c.B, 255);
            }
            return new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, 255);
        }

        private TermColor ResolvePaletteIndex(int index)
        {
            // 0-15 come from the active theme palette.
            if (index < 16)
            {
                return _buffer.Theme.GetAnsiColor(index % 8, index >= 8);
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
