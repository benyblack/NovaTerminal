using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
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
            int cursorCol = 0)
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
        }

        public void Dispose()
        {
            _skTypeface?.Dispose();
            _skFont?.Dispose();
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
                using var imagePaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), FilterQuality = SKFilterQuality.High };
                foreach (var img in _buffer.Images)
                {
                    int visualY = img.CellY - absDisplayStart;

                    // Only render if image is at least partially in viewport
                    if (visualY + img.CellHeight > 0 && visualY < bufferRows)
                    {
                        float x = (float)(Math.Round((img.CellX * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                        float y = (float)(Math.Round((visualY * _metrics.CellHeight + paddingTop) * _renderScaling) / _renderScaling);
                        float w = (float)(Math.Round((img.CellWidth * _metrics.CellWidth) * _renderScaling) / _renderScaling);
                        float h = (float)(Math.Round((img.CellHeight * _metrics.CellHeight) * _renderScaling) / _renderScaling);

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
                    float baselineY = (float)(Math.Round((r * _metrics.CellHeight + paddingTop + _metrics.Baseline) * _renderScaling) / _renderScaling);
                    dirtyCells += DrawRowText(canvas, r, absRow, bufferCols, baselineY, font, fgPaint, themeFg, themeBg, alpha, tf);
                }

                if (!_hideCursor)
                {
                    int absCursorRow = (_totalLines - _bufferRows) + _cursorRow;
                    int displayStart = Math.Max(0, _totalLines - _bufferRows - _scrollOffset);
                    int visualRow = absCursorRow - displayStart;
                    if (visualRow >= 0 && visualRow < bufferRows)
                    {
                        float x1 = (float)(Math.Round((_cursorCol * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                        float x2 = (float)(Math.Round(((_cursorCol + 1) * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                        float cy = (float)(Math.Round((visualRow * _metrics.CellHeight + _metrics.CellHeight - 2 + paddingTop) * _renderScaling) / _renderScaling);
                        canvas.DrawRect(x1, cy, x2 - x1, 2, new SKPaint { Color = new SKColor(255, 255, 255, alpha) });
                    }
                }
                RendererStatistics.RecordFrame(fullRedraw: true, dirtyCells: dirtyCells);
            }
            finally { _buffer.Lock.ExitReadLock(); }
        }

        private int DrawRowText(SKCanvas canvas, int r, int absRow, int bufferCols, float baselineY, SKFont font, SKPaint fgPaint, SKColor themeFg, SKColor themeBg, byte alpha, SKTypeface primaryTf)
        {
            float paddingLeft = 4f;
            int cellsRendered = 0;
            StringBuilder runText = new StringBuilder(bufferCols);

            for (int c = 0; c < bufferCols; c++)
            {
                var cell = _buffer.GetCellAbsolute(c, absRow);
                if (cell.IsWideContinuation || cell.IsHidden) continue;

                bool hasChar = (cell.Text != null) ? cell.Text.Length > 0 : (cell.Character != ' ' && cell.Character != '\0');
                if (!hasChar) continue;

                var cb = cell.IsDefaultBackground ? themeBg : new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                var cf = cell.IsDefaultForeground ? themeFg : new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, alpha);
                var fg = cell.IsInverse ? cb : cf;

                // Ligature Batching (Phase 3.3)
                if (_enableLigatures && !cell.IsWide && string.IsNullOrEmpty(cell.Text))
                {
                    int runStart = c;
                    runText.Clear();
                    runText.Append(cell.Character);
                    int k = c + 1;
                    while (k < bufferCols)
                    {
                        var next = _buffer.GetCellAbsolute(k, absRow);
                        if (next.IsWide || next.IsWideContinuation || !string.IsNullOrEmpty(next.Text) || next.IsHidden) break;
                        if (next.Character == ' ' || next.Character == '\0') break;
                        var ncb = next.IsDefaultBackground ? themeBg : new SKColor(next.Background.R, next.Background.G, next.Background.B, alpha);
                        var ncf = next.IsDefaultForeground ? themeFg : new SKColor(next.Foreground.R, next.Foreground.G, next.Foreground.B, alpha);
                        if ((next.IsInverse ? ncb : ncf) != fg) break;
                        runText.Append(next.Character);
                        k++;
                    }
                    fgPaint.Color = fg;
                    float rx = (float)(Math.Round((runStart * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                    canvas.DrawText(runText.ToString(), rx, baselineY, font, fgPaint);
                    cellsRendered += (k - runStart);
                    c = k - 1;
                    continue;
                }

                if (cell.IsWideContinuation) continue;

                // Individual Handling with Width-0 support (Phase 3.4)
                string text = cell.Text ?? cell.Character.ToString();

                SKTypeface? tfToUse = primaryTf;
                bool needsEmojiFallback = false;
                int primaryCodepoint = 0;
                int w = _buffer.GetGraphemeWidth(text);
                foreach (var rune in text.EnumerateRunes())
                {
                    int cp = rune.Value;
                    if (primaryCodepoint == 0) primaryCodepoint = cp;

                    if ((cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF) || (cp == 0x200D))
                    {
                        needsEmojiFallback = true;
                        break;
                    }
                }

                if (needsEmojiFallback || (primaryCodepoint != 0 && !primaryTf.ContainsGlyph(primaryCodepoint)))
                {
                    // search fallback chain...
                    string lookupKey = needsEmojiFallback ? text : primaryCodepoint.ToString();

                    if (!_fallbackCache.TryGetValue(lookupKey, out tfToUse))
                    {
                        SKTypeface? found = null;

                        // SPECIAL: Force Segoe UI Emoji for any complex cluster to ensure ligatures
                        if (needsEmojiFallback)
                        {
                            found = SKTypeface.FromFamilyName("Segoe UI Emoji");
                            TerminalLogger.Log($"[RENDERER] Forcing Segoe UI Emoji for '{text}' (needsEmojiFallback={needsEmojiFallback})");
                        }

                        // If not forced or Segoe failed, try fallback chain
                        if (found == null)
                        {
                            foreach (var tfChain in _fallbackChain)
                            {
                                if (tfChain.ContainsGlyph(primaryCodepoint))
                                {
                                    found = tfChain;
                                    break;
                                }
                            }
                        }

                        if (found == null)
                        {
                            found = SKFontManager.Default.MatchCharacter(primaryCodepoint);
                        }

                        _fallbackCache.TryAdd(lookupKey, found);
                        tfToUse = found;
                    }
                }

                if (tfToUse == null) tfToUse = primaryTf;

                fgPaint.Color = fg;
                float x = (float)(Math.Round((c * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                if (w == 0 && c > 0)
                {
                    // Back up to the base cell if we're over a wide continuation (rare for width-0 but supported)
                    int baseCol = c - 1;
                    while (baseCol > 0 && _buffer.GetCellAbsolute(baseCol, absRow).IsWideContinuation) baseCol--;
                    x = (float)(Math.Round((baseCol * _metrics.CellWidth + paddingLeft) * _renderScaling) / _renderScaling);
                }



                // PERFORMANCE OPTIMIZATION: Only use SaveLayer for wide/emoji characters
                // most terminal text is width-1 and doesn't need this overhead.
                bool useLayer = w > 1 || needsEmojiFallback;

                if (useLayer)
                {
                    // CLIPPING FIX: Use SaveLayer for strict isolation
                    // We add a tiny bit of extra width (2px scaled) to prevent aggressive clipping of modifiers
                    float clipWidth = (float)(Math.Round((w * _metrics.CellWidth) * _renderScaling + 2.0) / _renderScaling);
                    var layerRect = new SKRect(x, 0, x + clipWidth, (float)Bounds.Height);
                    canvas.SaveLayer(layerRect, null);
                }

                if (tfToUse != null && tfToUse != primaryTf)
                {
                    using var fFont = new SKFont(tfToUse, (float)_fontSize);
                    fFont.Edging = SKFontEdging.Antialias;
                    canvas.DrawText(text, x, baselineY, fFont, fgPaint);
                }
                else
                {
                    canvas.DrawText(text, x, baselineY, font, fgPaint);
                }

                if (useLayer)
                {
                    canvas.Restore();
                }

                cellsRendered += w;
            }
            return cellsRendered;
        }
    }
}
