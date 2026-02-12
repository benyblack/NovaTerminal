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
using System.Globalization;
using System.Text;
using System.Linq;

namespace NovaTerminal.Core
{
    public class TerminalDrawOperation : ICustomDrawOperation
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
        private List<SKRect> _alphaRects = new();
        private List<SKRotationScaleMatrix> _alphaXforms = new();
        private List<SKColor> _alphaColors = new();
        private List<SKRect> _colorRects = new();
        private List<SKRotationScaleMatrix> _colorXforms = new();

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

        private void DrawTerminal(SKCanvas canvas)
        {
            if (_buffer == null) return;

            _buffer.Lock.EnterReadLock();
            try
            {
                int bufferRows = _bufferRows;
                int bufferCols = _bufferCols;

                using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill };
                using var fgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                using var selectionPaint = new SKPaint { Color = new SKColor(51, 153, 255, 100) };
                using var matchPaint = new SKPaint { Color = new SKColor(255, 255, 0, 100) };
                using var activeMatchPaint = new SKPaint { Color = new SKColor(255, 128, 0, 150) };

                byte alpha = (byte)(255 * _opacity);
                var themeBg = new SKColor(_buffer.Theme.Background.R, _buffer.Theme.Background.G, _buffer.Theme.Background.B, alpha);
                var themeFg = new SKColor(_buffer.Theme.Foreground.R, _buffer.Theme.Foreground.G, _buffer.Theme.Foreground.B, alpha);

                if (_transparentBackground) themeBg = SKColors.Empty;

                bgPaint.Color = themeBg;
                canvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, bgPaint);


                float paddingLeft = 4f;
                float paddingTop = 0;
                int absDisplayStart = Math.Max(0, _totalLines - _bufferRows - _scrollOffset);

                // Note: We use the snapshotted _totalLines and _bufferRows for the background/text pass
                // to match the snapshot state. However, we should consider if images need a more 
                // "live" view if they are being added rapidly. 
                // For now, consistent snapshots are safer for a single frame.

                int dirtyCells = 0;

                // Pass 1: Backgrounds
                for (int r = 0; r < bufferRows; r++)
                {
                    float y = (float)(Math.Round((r * _metrics.CellHeight + paddingTop) * _renderScaling) / _renderScaling);
                    int absRow = absDisplayStart + r;

                    for (int c = 0; c < bufferCols; c++)
                    {
                        var cell = _buffer.GetCellAbsolute(c, absRow);
                        var cellBg = cell.IsDefaultBackground ? themeBg : new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                        var cellFg = cell.IsDefaultForeground ? themeFg : new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, alpha);
                        if (cell.IsInverse) { var tmp = cellBg; cellBg = cellFg; cellFg = tmp; }

                        if (cellBg != themeBg)
                        {
                            int runStart = c;
                            int k = c + 1;
                            while (k < bufferCols)
                            {
                                var n = _buffer.GetCellAbsolute(k, absRow);
                                var nb = n.IsDefaultBackground ? themeBg : new SKColor(n.Background.R, n.Background.G, n.Background.B, alpha);
                                var nf = n.IsDefaultForeground ? themeFg : new SKColor(n.Foreground.R, n.Foreground.G, n.Foreground.B, alpha);
                                if (n.IsInverse) nb = nf;
                                if (nb != cellBg) break;
                                k++;
                            }
                            bgPaint.Color = cellBg;
                            float x1 = (float)(Math.Round((runStart * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            float x2 = (float)(Math.Round((k * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            canvas.DrawRect(x1, y, x2 - x1, (float)_metrics.CellHeight, bgPaint);
                            dirtyCells += (k - runStart);
                            c = k - 1;
                        }
                    }

                    // Selection/Matches (also backgrounds)
                    if (_selection.IsActive)
                    {
                        var (isSelected, colStart, colEnd) = _selection.GetSelectionRangeForRow(absRow, bufferCols);
                        if (isSelected)
                        {
                            float x1 = (float)(Math.Round((colStart * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            float x2 = (float)(Math.Round(((colEnd + 1) * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            canvas.DrawRect(x1, y, x2 - x1, (float)_metrics.CellHeight, selectionPaint);
                        }
                    }
                    if (_searchMatches != null)
                    {
                        for (int i = 0; i < _searchMatches.Count; i++)
                        {
                            var m = _searchMatches[i];
                            if (m.AbsRow == absRow)
                            {
                                var p = (i == _activeSearchIndex) ? activeMatchPaint : matchPaint;
                                float x1 = (float)(Math.Round((m.StartCol * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                                float x2 = (float)(Math.Round(((m.EndCol + 1) * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                                canvas.DrawRect(x1, y, x2 - x1, (float)_metrics.CellHeight, p);
                            }
                        }
                    }
                }

                // Pass 2: Images
                if (_buffer.Images.Count > 0)
                {
                    TerminalLogger.Log($"[RENDERER] Drawing {_buffer.Images.Count} images. absDisplayStart={absDisplayStart}");
                }
                using var imagePaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
                foreach (var img in _buffer.Images)
                {
                    int visualY = img.CellY - absDisplayStart;

                    // Only render if image is at least partially in viewport
                    if (visualY + img.CellHeight > 0 && visualY < bufferRows)
                    {
                        float x = (float)(Math.Round((img.CellX * _metrics.CellWidth + paddingLeft) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float y = (float)(Math.Round((visualY * _metrics.CellHeight + paddingTop) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float w = (float)(Math.Round((img.CellWidth * _metrics.CellWidth) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float h = (float)(Math.Round((img.CellHeight * _metrics.CellHeight) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

                        var rect = new SKRect(x, y, x + w, y + h);
                        // TerminalLogger.Log($"[RENDERER] Drawing image at CellX={img.CellX}, CellY={img.CellY} -> visualY={visualY}, rect={rect.Left:F1},{rect.Top:F1},{rect.Width:F1}x{rect.Height:F1}, alpha={alpha}, scaling={_renderScaling}");

                        // Clip to terminal bounds
                        canvas.Save();
                        canvas.ClipRect(new SKRect(paddingLeft, 0, (float)Bounds.Width, (float)Bounds.Height));
                        canvas.DrawImage(img.Image, rect, imagePaint);
                        canvas.Restore();
                    }
                }

                // Pass 3: Text
                var tf = _skTypeface?.Typeface ?? SKTypeface.FromFamilyName(_typeface.FontFamily.Name);
                using var font = (_skFont?.Font != null) ? new SKFont(_skFont.Font.Typeface, _skFont.Font.Size) : new SKFont(tf, (float)_fontSize);
                font.Edging = SKFontEdging.Antialias;

                for (int r = 0; r < bufferRows; r++)
                {
                    int absRow = absDisplayStart + r;
                    float y = (float)(Math.Round((r * _metrics.CellHeight + paddingTop) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                    float baselineY = (float)(Math.Round((r * _metrics.CellHeight + paddingTop + _metrics.Baseline) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

                    // CACHE CHECK:
                    var row = _buffer.GetRowAbsolute(absRow);
                    if (_rowCache != null && row != null)
                    {
                        var cachedPicture = _rowCache.Get(absRow, row.Revision);
                        if (cachedPicture != null)
                        {
                            canvas.DrawPicture(cachedPicture, 0, y);
                            dirtyCells += bufferCols; // Count as rendered
                            continue;
                        }

                        // CACHE MISS: Record the row as vector commands (SKPicture)
                        // This ensures 100% sharpness regardless of DPI scaling.
                        using var recorder = new SKPictureRecorder();
                        using var rowCanvas = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, (float)_metrics.CellHeight));

                        // Draw text at logical baseline 0..CellHeight
                        DrawRowText(rowCanvas, r, absRow, bufferCols, (float)_metrics.Baseline, font, fgPaint, themeFg, themeBg, alpha, tf);

                        var snapshot = recorder.EndRecording();
                        _rowCache.Add(absRow, row.Revision, snapshot);
                        canvas.DrawPicture(snapshot, 0, y);
                        dirtyCells += bufferCols;
                        continue;
                    }

                    dirtyCells += DrawRowText(canvas, r, absRow, bufferCols, baselineY, font, fgPaint, themeFg, themeBg, alpha, tf);
                }

                if (!_hideCursor)
                {
                    int absCursorRow = (_totalLines - _bufferRows) + _cursorRow;
                    int displayStart = Math.Max(0, _totalLines - _bufferRows - _scrollOffset);
                    int visualRow = absCursorRow - displayStart;
                    if (visualRow >= 0 && visualRow < bufferRows)
                    {
                        float x1 = (float)(Math.Round((_cursorCol * _metrics.CellWidth + paddingLeft) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float x2 = (float)(Math.Round(((_cursorCol + 1) * _metrics.CellWidth + paddingLeft) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float cy = (float)(Math.Round((visualRow * _metrics.CellHeight + _metrics.CellHeight - 2 + paddingTop) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        canvas.DrawRect(x1, cy, x2 - x1, 2, new SKPaint { Color = new SKColor(255, 255, 255, alpha) });
                    }
                }
                RendererStatistics.RecordFrame(fullRedraw: true, dirtyCells: dirtyCells);
            }
            finally { _buffer.Lock.ExitReadLock(); }
        }

        private void FlushBatches(SKCanvas canvas)
        {
            if (_glyphCache == null) return;
            var (alphaAtlas, colorAtlas) = _glyphCache.GetAtlasImages();

            // Flush Alpha Batch
            if (_alphaRects.Count > 0)
            {
                using var paint = new SKPaint { IsAntialias = true };
                // Use Linear sampling for smoother alpha transition on fractional High-DPI.
                canvas.DrawAtlas(alphaAtlas, _alphaRects.ToArray(), _alphaXforms.ToArray(), _alphaColors.ToArray(), SKBlendMode.Modulate, new SKSamplingOptions(SKFilterMode.Linear), paint);
                _alphaRects.Clear();
                _alphaXforms.Clear();
                _alphaColors.Clear();
            }

            // Flush Color Batch (Emojis)
            if (_colorRects.Count > 0)
            {
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawAtlas(colorAtlas, _colorRects.ToArray(), _colorXforms.ToArray(), null, SKBlendMode.SrcOver, new SKSamplingOptions(SKFilterMode.Linear), paint);
                _colorRects.Clear();
                _colorXforms.Clear();
            }
        }

        private int DrawRowText(SKCanvas canvas, int r, int absRow, int bufferCols, float baselineY, SKFont font, SKPaint fgPaint, SKColor themeFg, SKColor themeBg, byte alpha, SKTypeface primaryTf)
        {
            float paddingLeft = 4f;
            int cellsRendered = 0;

            for (int c = 0; c < bufferCols; c++)
            {
                var cell = _buffer.GetCellAbsolute(c, absRow);
                if (cell.IsWideContinuation || cell.IsHidden) continue;

                var cb = cell.IsDefaultBackground ? themeBg : new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                var cf = cell.IsDefaultForeground ? themeFg : new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, alpha);
                var fg = cell.IsInverse ? cb : cf;
                var bg = cell.IsInverse ? cf : cb;

                // Identify the formatting run (same FG and BG)
                StringBuilder runBuilder = new StringBuilder();
                int totalRunWidth = 0;
                bool runNeedsComplexShaping = false;
                int k = c;

                while (k < bufferCols)
                {
                    var next = _buffer.GetCellAbsolute(k, absRow);
                    if (next.IsHidden || next.IsWideContinuation) { k++; continue; }

                    var ncb = next.IsDefaultBackground ? themeBg : new SKColor(next.Background.R, next.Background.G, next.Background.B, alpha);
                    var ncf = next.IsDefaultForeground ? themeFg : new SKColor(next.Foreground.R, next.Foreground.G, next.Foreground.B, alpha);
                    var nfg = next.IsInverse ? ncb : ncf;
                    var nbg = next.IsInverse ? ncf : ncb;

                    if (nfg != fg || nbg != bg) break;

                    string cellText = next.Text ?? next.Character.ToString();
                    if (_enableComplexShaping)
                    {
                        foreach (var rune in cellText.EnumerateRunes())
                        {
                            int cp = rune.Value;
                            if ((cp >= 0x0590 && cp <= 0x0FFF) || // Complex scripts
                                (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF) || (cp == 0x200D))
                            {
                                runNeedsComplexShaping = true;
                            }
                        }
                    }

                    runBuilder.Append(cellText);
                    totalRunWidth += _buffer.GetGraphemeWidth(cellText);
                    k++;
                }

                string runText = runBuilder.ToString();
                // Physical snapping for coordinate base
                float rx = (float)(Math.Round((c * _metrics.CellWidth + paddingLeft) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                fgPaint.Color = fg;

                // Only draw background if we are not on the main canvas pass (Pass 1 already handles it)
                // OR if we are recording for the RowCache.
                bool isRecording = canvas.GetType().Name.Contains("Picture") || canvas.GetType().Name.Contains("Proxy");
                if (bg != themeBg && bg != SKColors.Transparent)
                {
                    using var bgP = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
                    float rw = (float)(Math.Round((totalRunWidth * _metrics.CellWidth) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                    canvas.DrawRect(rx, 0, rw, (float)_metrics.CellHeight, bgP);
                }

                if (runNeedsComplexShaping)
                {
                    FlushBatches(canvas);
                    SKTypeface? tfToUse = primaryTf;
                    int fallbackChar = 0;
                    foreach (var rune in runText.EnumerateRunes())
                    {
                        int cp = rune.Value;
                        if (cp > 127 && !primaryTf.ContainsGlyph(cp)) { fallbackChar = cp; break; }
                    }

                    if (fallbackChar != 0)
                    {
                        string lookupKey = fallbackChar.ToString();
                        if (!_fallbackCache.TryGetValue(lookupKey, out tfToUse))
                        {
                            SKTypeface? found = null;
                            if ((fallbackChar >= 0x1F300 && fallbackChar <= 0x1FAFF) || (fallbackChar >= 0x2600 && fallbackChar <= 0x27BF))
                                found = SKTypeface.FromFamilyName("Segoe UI Emoji");
                            if (found == null)
                            {
                                foreach (var tfChain in _fallbackChain)
                                    if (tfChain.ContainsGlyph(fallbackChar)) { found = tfChain; break; }
                            }
                            if (found == null) found = SKFontManager.Default.MatchCharacter(fallbackChar);
                            _fallbackCache.TryAdd(lookupKey, found);
                            tfToUse = found;
                        }
                    }

                    bool useLayer = totalRunWidth > 1;
                    if (useLayer)
                    {
                        float clipWidth = (float)(Math.Round((totalRunWidth * _metrics.CellWidth) * _renderScaling + 0.5) / _renderScaling);
                        canvas.SaveLayer(new SKRect(rx, 0, rx + clipWidth, (float)Bounds.Height), null);
                    }

                    using var fFont = new SKFont(tfToUse ?? primaryTf, (float)_fontSize);
                    fFont.Edging = SKFontEdging.Antialias;
                    using var shaper = new SKShaper(tfToUse ?? primaryTf);
                    canvas.DrawShapedText(shaper, runText, rx, (float)_metrics.Baseline, fFont, fgPaint);

                    if (useLayer) canvas.Restore();
                }
                else
                {
                    // SIMPLE RUN - Physics-Perfect Atlas Rendering
                    if (_glyphCache != null)
                    {
                        // logical coordinate accumulator
                        float xIdxLogical = c * (float)_metrics.CellWidth + paddingLeft;
                        float yBaselineSnap = (float)(Math.Round((float)_metrics.Baseline * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);
                        float glyphY = (float)(Math.Round((yBaselineSnap + font.Metrics.Ascent) * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

                        // Compensatory scale: since atlas has physical size, we must scale by 1/scaling
                        // because the canvas itself is already scaled by 'scaling' during Avalonia rendering.
                        float invScale = (float)(1.0 / _renderScaling);

                        foreach (var rune in runText.EnumerateRunes())
                        {
                            string grapheme = rune.ToString();
                            var cached = _glyphCache.GetOrAdd(grapheme, font, (float)_renderScaling);
                            float xIdxSnap = (float)(Math.Round(xIdxLogical * _renderScaling, MidpointRounding.AwayFromZero) / _renderScaling);

                            if (cached != null)
                            {
                                var (rect, type) = cached.Value;
                                // Matrix: Transform = [Scale] * [Translation]
                                // Note: We use invScale to achieve 1:1 pixel mapping.
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
                                canvas.DrawText(grapheme, xIdxSnap, yBaselineSnap, font, fgPaint);
                            }

                            xIdxLogical += (float)_metrics.CellWidth * (float)_buffer.GetGraphemeWidth(grapheme);
                        }
                    }
                    else
                    {
                        canvas.DrawText(runText, rx, (float)_metrics.Baseline, font, fgPaint);
                    }
                }

                cellsRendered += totalRunWidth;
                c = k - 1;
            }

            FlushBatches(canvas);
            return cellsRendered;
        }
    }
}
