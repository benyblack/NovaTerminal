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

        // Step 5: Smooth out rendering spikes
        private const int MaxPictureBuildsPerFrame = 4;
        private const float MassInvalidationThreshold = 0.5f;

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
                    frame.ThemeFg = new SKColor(_buffer.Theme.Foreground.R, _buffer.Theme.Foreground.G, _buffer.Theme.Foreground.B, alpha);
                    if (_transparentBackground) frame.ThemeBg = SKColors.Empty;

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
                            // Cache probe
                            item.CachedPicture = _rowCache?.Get(absRow, row.Revision);

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

                // Base background (window)
                bgPaint.Color = frame.ThemeBg;
                canvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, bgPaint);

                float paddingLeft = 4f;
                float paddingTop = 0f;

                int dirtyCells = 0;
                int buildsThisFrame = 0;
                int maxBuilds = isMassInvalidation ? 0 : MaxPictureBuildsPerFrame;

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
                            _rowCache?.Add(item.AbsRow, snapshot.Revision, picture);
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
                            canvas.DrawImage(img.Image, rect, imagePaint);
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
                        float cy = SnapY(visualRow * _metrics.CellHeight + paddingTop + _metrics.CellHeight - 2);

                        using var cursorPaint = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, alpha),
                            Style = SKPaintStyle.Fill
                        };

                        canvas.DrawRect(x1, cy, x2 - x1, 2, cursorPaint);
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
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawAtlas(
                    alphaAtlas,
                    _alphaRects.ToArray(),
                    _alphaXforms.ToArray(),
                    _alphaColors.ToArray(),
                    SKBlendMode.Modulate,
                    new SKSamplingOptions(SKFilterMode.Linear),
                    paint);

                _alphaRects.Clear();
                _alphaXforms.Clear();
                _alphaColors.Clear();
            }

            if (_colorRects.Count > 0)
            {
                using var paint = new SKPaint { IsAntialias = true };
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

                var cb = cell.IsDefaultBackground ? themeBg : new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                var cf = cell.IsDefaultForeground ? themeFg : new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, alpha);
                var fg = cell.IsInverse ? cb : cf;
                var bg = cell.IsInverse ? cf : cb;

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

                    var ncb = next.IsDefaultBackground ? themeBg : new SKColor(next.Background.R, next.Background.G, next.Background.B, alpha);
                    var ncf = next.IsDefaultForeground ? themeFg : new SKColor(next.Foreground.R, next.Foreground.G, next.Foreground.B, alpha);
                    var nfg = next.IsInverse ? ncb : ncf;
                    var nbg = next.IsInverse ? ncf : ncb;

                    if (nfg != fg || nbg != bg)
                        break;

                    string cellText = next.Text ?? next.Character.ToString();

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
                    int w = _buffer.GetGraphemeWidth(cellText); // GetGraphemeWidth is thread-safe
                    totalRunWidth += w;
                    k++;
                }

                string runText = runBuilder.ToString();
                float rx = SnapX(c * _metrics.CellWidth + paddingLeft);
                fgPaint.Color = fg;

                // Backgrounds (when requested)
                if (drawBackgrounds && bg != themeBg && bg.Alpha != 0)
                {
                    using var bgP = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
                    float rw = (float)(Math.Round((totalRunWidth * _metrics.CellWidth) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                    canvas.DrawRect(rx, rowTopY, rw, (float)_metrics.CellHeight, bgP);
                }

                if (runNeedsComplexShaping)
                {
                    FlushBatches(canvas);
                    SKTypeface? tfToUse = primaryTf;
                    int fallbackChar = 0;
                    foreach (var rune in runText.EnumerateRunes())
                    {
                        int cp = rune.Value;
                        if (cp > 127 && !primaryTf.ContainsGlyph(cp))
                        {
                            fallbackChar = cp;
                            break;
                        }
                    }

                    if (fallbackChar != 0)
                    {
                        string lookupKey = fallbackChar.ToString();
                        if (!_fallbackCache.TryGetValue(lookupKey, out tfToUse))
                        {
                            SKTypeface? found = null;
                            if ((fallbackChar >= 0x1F300 && fallbackChar <= 0x1FAFF) ||
                                (fallbackChar >= 0x2600 && fallbackChar <= 0x27BF))
                            {
                                found = SKTypeface.FromFamilyName("Segoe UI Emoji");
                            }
                            if (found == null)
                            {
                                foreach (var tfChain in _fallbackChain)
                                {
                                    if (tfChain.ContainsGlyph(fallbackChar))
                                    {
                                        found = tfChain;
                                        break;
                                    }
                                }
                            }
                            found ??= SKFontManager.Default.MatchCharacter(fallbackChar);
                            _fallbackCache.TryAdd(lookupKey, found);
                            tfToUse = found;
                        }
                    }

                    bool useLayer = totalRunWidth > 1;
                    if (useLayer)
                    {
                        float clipWidth = (float)(Math.Round((totalRunWidth * _metrics.CellWidth) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        canvas.SaveLayer(new SKRect(rx, rowTopY, rx + clipWidth, rowTopY + (float)_metrics.CellHeight), null);
                    }

                    using var fFont = new SKFont(tfToUse ?? primaryTf, (float)_fontSize) { Edging = SKFontEdging.Antialias };
                    using var shaper = new SKShaper(tfToUse ?? primaryTf);
                    canvas.DrawShapedText(shaper, runText, rx, baselineY, fFont, fgPaint);

                    if (useLayer) canvas.Restore();
                    cellsRendered += totalRunWidth;
                }
                else
                {
                    if (_glyphCache != null)
                    {
                        float xIdxLogical = c * (float)_metrics.CellWidth + paddingLeft;
                        float yBaselineSnap = (float)(Math.Round(baselineY * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float glyphY = (float)(Math.Round((yBaselineSnap + font.Metrics.Ascent) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float invScale = (float)(1.0 / _renderScaling);

                        foreach (var rune in runText.EnumerateRunes())
                        {
                            string grapheme = rune.ToString();
                            var cached = _glyphCache.GetOrAdd(grapheme, font, (float)_renderScaling);
                            float xIdxSnap = (float)(Math.Round(xIdxLogical * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

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
                                canvas.DrawText(grapheme, xIdxSnap, yBaselineSnap, font, fgPaint);
                            }
                            xIdxLogical += (float)_metrics.CellWidth * _buffer.GetGraphemeWidth(grapheme);
                        }
                    }
                    else
                    {
                        canvas.DrawText(runText, rx, baselineY, font, fgPaint);
                    }
                    cellsRendered += totalRunWidth;
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

        private float SnapX(double logicalX)
            => (float)(Math.Round(logicalX * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

        private float SnapY(double logicalY)
            => (float)(Math.Round(logicalY * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
    }
}
