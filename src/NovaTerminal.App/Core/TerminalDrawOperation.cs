// TerminalDrawOperation.cs (drop-in replacement)
// Notes:
// - Preserves existing constructor signature used by TerminalView.Render(...)
// - Fixes baselineY usage + rowTopY background placement
// - Removes fragile "recording canvas" type checks (explicit drawBackgrounds flag)
// - Fixes active match detection WITHOUT IndexOf (avoids O(n^2))
// - Reuses StringBuilder per row to reduce GC churn
// - Keeps Z-order: Text -> Images -> Overlays -> Cursor
// - Keeps lock timing instrumentation as-is (Step 4 will shrink lock scope)

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System;
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

        // Batching for DrawAtlas
        private readonly List<SKRect> _alphaRects = new();
        private readonly List<SKRotationScaleMatrix> _alphaXforms = new();
        private readonly List<SKColor> _alphaColors = new();
        private readonly List<SKRect> _colorRects = new();
        private readonly List<SKRotationScaleMatrix> _colorXforms = new();

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
                            }
                            else
                            {
                                RendererStatistics.RecordRowCacheMiss();
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

                float paddingLeft = 4f;
                float paddingTop = 0f;

                int dirtyCells = 0;
                int buildsThisFrame = 0;
                int maxBuilds = MaxPictureBuildsPerFrame; // always unlimited

                // Pass 3: Text
                var tf = _skTypeface?.Typeface ?? SKTypeface.FromFamilyName(_typeface.FontFamily.Name);
                using var font = (_skFont?.Font != null)
                    ? new SKFont(_skFont.Font.Typeface, _skFont.Font.Size)
                    : new SKFont(tf, (float)_fontSize);

                font.Edging = SKFontEdging.Antialias;

                // Draw rows in visual order. RowItems count == bufferRows by construction.
                for (int r = 0; r < frame.RowItems.Count; r++)
                {
                    var item = frame.RowItems[r];

                    float rowTopY = SnapY(r * _metrics.CellHeight + paddingTop);
                    float baselineY = SnapY(r * _metrics.CellHeight + paddingTop + _metrics.Baseline);

                    if (item.CachedPicture != null)
                    {
                        canvas.DrawPicture(item.CachedPicture, 0, rowTopY);
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
                                baselineY: (float)_metrics.Baseline,
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
                {
                    foreach (var img in frame.Images)
                    {
                        int visualY = img.CellY - absDisplayStart;
                        if (visualY + img.CellHeight > 0 && visualY < bufferRows)
                        {
                            float x = SnapX(img.CellX * _metrics.CellWidth + paddingLeft);
                            float y = SnapY(visualY * _metrics.CellHeight + paddingTop);

                            float w = (float)(Math.Round((img.CellWidth * _metrics.CellWidth) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                            float h = (float)(Math.Round((img.CellHeight * _metrics.CellHeight) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

                            var rect = new SKRect(x, y, x + w, y + h);

                            canvas.Save();
                            canvas.ClipRect(new SKRect(paddingLeft, 0, (float)Bounds.Width, (float)Bounds.Height));
                            if (img.ImageHandle is SKBitmap bmp)
                            {
                                canvas.DrawBitmap(bmp, rect, imagePaint);
                            }
                            canvas.Restore();
                        }
                    }
                }

                // Pass 4: Overlays (Selection / Search)
                int matchIndex = 0;
                for (int r = 0; r < bufferRows; r++)
                {
                    float y = SnapY(r * _metrics.CellHeight + paddingTop);
                    int absRow = absDisplayStart + r;

                    if (_selection.IsActive)
                    {
                        var (isSelected, colStart, colEnd) = _selection.GetSelectionRangeForRow(absRow, bufferCols);
                        if (isSelected)
                        {
                            float x1 = SnapX(colStart * _metrics.CellWidth + paddingLeft);
                            float x2 = SnapX((colEnd + 1) * _metrics.CellWidth + paddingLeft);
                            canvas.DrawRect(x1, y, x2 - x1, (float)_metrics.CellHeight, selectionPaint);
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
                            float x1 = SnapX(m.StartCol * _metrics.CellWidth + paddingLeft);
                            float x2 = SnapX((m.EndCol + 1) * _metrics.CellWidth + paddingLeft);
                            canvas.DrawRect(x1, y, x2 - x1, (float)_metrics.CellHeight, p);
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
                        float x1 = SnapX(_cursorCol * _metrics.CellWidth + paddingLeft);
                        float x2 = SnapX((_cursorCol + 1) * _metrics.CellWidth + paddingLeft);
                        float rowTop = SnapY(visualRow * _metrics.CellHeight + paddingTop);
                        using var cursorPaint = new SKPaint { Color = frame.CursorColor, Style = SKPaintStyle.Fill };

                        switch (frame.CursorStyle)
                        {
                            case CursorStyle.Block:
                                canvas.DrawRect(x1, rowTop, x2 - x1, (float)_metrics.CellHeight, cursorPaint);
                                break;
                            case CursorStyle.Beam:
                                {
                                    float beamW = Math.Max(1f, (float)Math.Floor(_metrics.CellWidth * 0.14));
                                    canvas.DrawRect(x1, rowTop, beamW, (float)_metrics.CellHeight, cursorPaint);
                                    break;
                                }
                            case CursorStyle.Underline:
                            default:
                                {
                                    float uY = SnapY(rowTop + _metrics.CellHeight - 2);
                                    canvas.DrawRect(x1, uY, x2 - x1, 2, cursorPaint);
                                    break;
                                }
                        }
                    }
                }

                frameSw.Stop();
                RendererStatistics.RecordFrameRenderTime(frameSw.ElapsedMilliseconds);
                RendererStatistics.RecordFrame(fullRedraw: true, dirtyCells: dirtyCells);
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

            if (_alphaRects.Count > 0)
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

                _alphaRects.Clear();
                _alphaXforms.Clear();
                _alphaColors.Clear();
            }

            if (_colorRects.Count > 0)
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

                _colorRects.Clear();
                _colorXforms.Clear();
            }
        }

        private int DrawRowTextFromSnapshot(
            SKCanvas canvas,
            RenderRowSnapshot snapshot,
            float rowTopY,
            float baselineY,
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

            // Reuse per-row StringBuilder to avoid per-run allocations/GC churn.
            var runBuilder = new StringBuilder(capacity: Math.Max(16, snapshot.Cols));

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
                fg = EnsureReadableForeground(fg, bg, themeFg);
                if (runIsFaint)
                {
                    fg = BlendTowards(fg, bg, 0.5f);
                }

                runBuilder.Clear();
                int totalRunWidth = 0;
                bool runNeedsComplexShaping = false;
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
                    nfg = EnsureReadableForeground(nfg, nbg, themeFg);
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

                    if (_enableComplexShaping)
                    {
                        foreach (var rune in cellText.EnumerateRunes())
                        {
                            int cp = rune.Value;
                            if ((cp >= 0x0590 && cp <= 0x0FFF) || // Complex scripts
                                (cp >= 0x1F300 && cp <= 0x1FAFF) || // Emoji
                                (cp >= 0x2600 && cp <= 0x27BF) ||   // Dingbats/symbols
                                (cp == 0x200D))                      // ZWJ
                            {
                                runNeedsComplexShaping = true;
                                break;
                            }
                        }
                    }

                    runBuilder.Append(cellText);
                    int w = GetSafeGraphemeWidth(cellText); // GetGraphemeWidth is thread-safe
                    totalRunWidth += w;
                    k++;
                }

                string runText = runBuilder.ToString();
                float rx = SnapX(c * _metrics.CellWidth + paddingLeft);
                float rx2 = SnapX((c + totalRunWidth) * _metrics.CellWidth + paddingLeft);
                fgPaint.Color = fg;
                float rw = rx2 - rx;
                float strokeWidth = Math.Max(1f, (float)(_metrics.CellHeight * 0.06));

                // Backgrounds (when requested)
                if (drawBackgrounds && bg != themeBg && bg.Alpha != 0)
                {
                    using var bgP = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(rx, rowTopY, rw, (float)_metrics.CellHeight, bgP);
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
                    canvas.DrawShapedText(shaper, runText, rx, baselineY, fFont, fgPaint);

                    if (useLayer) canvas.Restore();
                    cellsRendered += totalRunWidth;
                }
                else
                {
                    if (_glyphCache != null && !runIsItalic)
                    {
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
                            double cellWidth = _metrics.CellWidth;
                            double xIdxLogical = (cellX * cellWidth) + paddingLeft;
                            float xIdxSnap = SnapX(xIdxLogical);

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

                            float cellX1 = xIdxSnap;
                            float cellX2 = SnapX(((cellX + graphemeWidth) * cellWidth) + paddingLeft);
                            float cellW = cellX2 - cellX1;
                            float cellH = (float)_metrics.CellHeight;

                            // Render primary block elements on the cell grid to avoid font side-bearing seams.
                            if (TryGetBlockFillRect(grapheme, out int xStartEighths, out int xEndEighths, out int yStartEighths, out int yEndEighths))
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
                                }

                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (TryGetShadeFillAlpha(grapheme, out float shadeAlpha))
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
                                }

                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (TryGetQuadrantFillMask(grapheme, out byte quadrantMask))
                            {
                                FlushBatches(canvas);
                                DrawQuadrantSubcells(canvas, quadrantMask, cellX1, rowTopY, cellW, cellH, blockPaint);
                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (TryGetBraillePattern(grapheme, out byte brailleMask))
                            {
                                FlushBatches(canvas);
                                DrawBrailleSubcells(canvas, brailleMask, cellX1, rowTopY, cellW, cellH, blockPaint);
                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (ShouldForceDirectTextForGraphGlyph(grapheme))
                            {
                                FlushBatches(canvas);
                                canvas.DrawText(grapheme, xIdxSnap, yBaselineSnap, glyphFont, fgPaint);
                                fallbackFont?.Dispose();
                                cellX += graphemeWidth;
                                continue;
                            }

                            if (ShouldBypassGlyphAtlasForGrapheme(grapheme))
                            {
                                FlushBatches(canvas);
                                canvas.DrawText(grapheme, xIdxSnap, yBaselineSnap, glyphFont, fgPaint);
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
                            }
                            fallbackFont?.Dispose();
                            cellX += graphemeWidth;
                        }
                    }
                    else
                    {
                        canvas.DrawText(runText, rx, baselineY, font, fgPaint);
                    }
                    cellsRendered += totalRunWidth;
                }

                if (appliedItalicTransform)
                {
                    canvas.Restore();
                }

                if (runIsUnderline || runIsStrikethrough)
                {
                    using var decoPaint = new SKPaint
                    {
                        Color = fg,
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = strokeWidth
                    };

                    if (runIsUnderline)
                    {
                        float underlineY = SnapY(rowTopY + _metrics.CellHeight - Math.Max(1.5, _metrics.CellHeight * 0.12));
                        canvas.DrawLine(rx, underlineY, rx + rw, underlineY, decoPaint);
                    }

                    if (runIsStrikethrough)
                    {
                        float strikeY = SnapY(rowTopY + (_metrics.CellHeight * 0.52));
                        canvas.DrawLine(rx, strikeY, rx + rw, strikeY, decoPaint);
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

        private float Snap(double logical)
            => (float)(Math.Round(logical * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

        private float SnapX(double logicalX) => Snap(logicalX);

        private float SnapY(double logicalY) => Snap(logicalY);

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
            if (codePoint <= 127 || primaryTf.ContainsGlyph(codePoint))
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

        private static bool ShouldBypassGlyphAtlasForGrapheme(string grapheme)
        {
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;

            if (cp >= 0x2500 && cp <= 0x257F) return true; // Box drawing
            if (cp >= 0x25A0 && cp <= 0x25FF) return true; // Geometric block symbols

            return false;
        }

        private static bool ShouldForceDirectTextForGraphGlyph(string grapheme)
        {
            if (!TryGetSingleRuneCodePoint(grapheme, out int cp)) return false;

            if (cp >= 0x2500 && cp <= 0x257F) return true; // Box drawing
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
