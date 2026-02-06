using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class TerminalDrawOperation : ICustomDrawOperation
    {
        private readonly TerminalBuffer _buffer;
        private readonly double _charWidth;
        private readonly double _charHeight;
        private readonly double _baselineOffset;
        private readonly int _scrollOffset;
        private readonly SelectionState _selection;
        private readonly List<SearchMatch>? _searchMatches;
        private readonly int _activeSearchIndex;
        private readonly Typeface _typeface;
        private readonly double _fontSize;
        private readonly Rect _bounds;
        private readonly IGlyphTypeface _glyphTypeface;
        private readonly SKTypeface? _skTypeface;
        private readonly SKFont? _skFont;
        private readonly float _opacity;
        private readonly bool _transparentBackground;
        private readonly bool _hideCursor;
        private readonly double _renderScaling;

        public Rect Bounds => _bounds;

        public TerminalDrawOperation(
            Rect bounds,
            TerminalBuffer buffer,
            int scrollOffset,
            SelectionState selection,
            List<SearchMatch>? searchMatches,
            int activeSearchIndex,
            double charWidth,
            double charHeight,
            double baselineOffset,
            Typeface typeface,
            double fontSize,
            IGlyphTypeface glyphTypeface,
            SKTypeface? skTypeface,
            SKFont? skFont,
            double opacity = 1.0,
            bool transparentBackground = false,
            bool hideCursor = false,
            double renderScaling = 1.0)
        {
            _bounds = bounds;
            _buffer = buffer;
            _scrollOffset = scrollOffset;
            _selection = selection;
            _searchMatches = searchMatches;
            _activeSearchIndex = activeSearchIndex;
            _charWidth = charWidth;
            _charHeight = charHeight;
            _baselineOffset = baselineOffset;
            _typeface = typeface;
            _fontSize = fontSize;
            _glyphTypeface = glyphTypeface;
            _skTypeface = skTypeface;
            _skFont = skFont;
            _opacity = (float)Math.Clamp(opacity, 0.0, 1.0);

            _transparentBackground = transparentBackground;
            _hideCursor = hideCursor;
            _renderScaling = renderScaling;
        }

        public void Dispose()
        {
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
                // CRITICAL: Capture dimensions at render start to avoid race conditions
                // If buffer is resized mid-render, using _buffer.Rows/Cols directly could cause
                // reading garbage data or out-of-bounds access.
                int bufferRows = _buffer.Rows;
                int bufferCols = _buffer.Cols;

                // Setup paints
                using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill };
                using var fgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                using var selectionPaint = new SKPaint { Color = new SKColor(51, 153, 255, 100) };
                using var matchPaint = new SKPaint { Color = new SKColor(255, 255, 0, 100) };
                using var activeMatchPaint = new SKPaint { Color = new SKColor(255, 128, 0, 150) };

                // Calculate alpha from opacity setting for ALL terminal colors
                byte alpha = (byte)(255 * _opacity);

                var themeBg = new SKColor(_buffer.Theme.Background.R, _buffer.Theme.Background.G, _buffer.Theme.Background.B, alpha);
                var themeFg = new SKColor(_buffer.Theme.Foreground.R, _buffer.Theme.Foreground.G, _buffer.Theme.Foreground.B, alpha);

                // If background image is active, we force the theme background to be transparent
                // so the image shows through.
                if (_transparentBackground)
                {
                    themeBg = SKColors.Empty;
                }

                // Fill background with semi-transparent color (matches WindowBackground alpha)
                bgPaint.Color = themeBg;
                canvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, bgPaint);

                var tf = _skTypeface ?? SKTypeface.FromFamilyName(_typeface.FontFamily.Name);
                using var font = new SKFont(tf, (float)_fontSize);
                font.Edging = SKFontEdging.Antialias;

                // Padding for terminal content (Logical 4 units)
                float paddingLeft = 4f;
                float paddingTop = 0;

                // Correct calculation of displayStart (First visible absolute row)
                int totalLines = _buffer.TotalLines;
                int bufferRowsLocked = bufferRows; // Use local capture
                                                   // displayStart = Total - Rows - ScrollOffset
                int absDisplayStart = Math.Max(0, totalLines - bufferRowsLocked - _scrollOffset);

                int dirtyCells = 0;
                for (int r = 0; r < bufferRows; r++)
                {
                    float y = (float)(Math.Round((r * _charHeight + paddingTop) * _renderScaling) / _renderScaling);
                    float baselineY = y + (float)_baselineOffset;
                    int absRow = absDisplayStart + r;

                    // Pass 1: Custom Backgrounds
                    for (int c = 0; c < bufferCols; c++)
                    {
                        var cell = _buffer.GetCell(c, r, _scrollOffset);
                        var cellBg = cell.IsDefaultBackground ? themeBg :
                                    new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                        var cellFg = cell.IsDefaultForeground ? themeFg :
                                    new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, alpha);

                        if (cell.IsInverse) { var tmp = cellBg; cellBg = cellFg; cellFg = tmp; }

                        if (cellBg != themeBg)
                        {
                            var bg = cellBg;
                            int runStart = c;
                            int k = c + 1;
                            while (k < bufferCols)
                            {
                                var nextCell = _buffer.GetCell(k, r, _scrollOffset);
                                var nextBg = nextCell.IsDefaultBackground ? themeBg :
                                            new SKColor(nextCell.Background.R, nextCell.Background.G, nextCell.Background.B, alpha);
                                var nextFg = nextCell.IsDefaultForeground ? themeFg :
                                            new SKColor(nextCell.Foreground.R, nextCell.Foreground.G, nextCell.Foreground.B, alpha);
                                if (nextCell.IsInverse) nextBg = nextFg;

                                if (nextBg != bg) break;
                                k++;
                            }

                            bgPaint.Color = bg;
                            float x1 = (float)(Math.Round((runStart * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            float x2 = (float)(Math.Round((k * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            canvas.DrawRect(x1, y, x2 - x1, (float)_charHeight, bgPaint);
                            dirtyCells += (k - runStart);
                            c = k - 1;
                        }
                    }

                    // Selection & Search Matches
                    if (_selection.IsActive)
                    {
                        var (isSelected, colStart, colEnd) = _selection.GetSelectionRangeForRow(absRow, bufferCols);
                        if (isSelected)
                        {
                            float x1 = (float)(Math.Round((colStart * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            float x2 = (float)(Math.Round(((colEnd + 1) * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                            canvas.DrawRect(x1, y, x2 - x1, (float)_charHeight, selectionPaint);
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
                                float x1 = (float)(Math.Round((m.StartCol * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                                float x2 = (float)(Math.Round(((m.EndCol + 1) * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                                canvas.DrawRect(x1, y, x2 - x1, (float)_charHeight, p);
                            }
                        }
                    }

                    // Pass 2: Foreground (Text)
                    for (int c = 0; c < bufferCols; c++)
                    {
                        var cell = _buffer.GetCell(c, r, _scrollOffset);

                        // Skip continuation cells (they are covered by the wide char in the previous cell)
                        if (cell.IsWideContinuation) continue;

                        // Respect Hidden Attribute
                        if (cell.IsHidden)
                        {
                            if (!cell.IsDefaultBackground)
                            {
                                var cb = new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                                bgPaint.Color = cb;
                                float x1 = (float)(Math.Round((c * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                                float x2 = (float)(Math.Round(((c + (cell.IsWide ? 2 : 1)) * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                                canvas.DrawRect(x1, y, x2 - x1, (float)_charHeight, bgPaint);
                            }
                            continue;
                        }

                        bool hasChar = (cell.Text != null) ? cell.Text.Length > 0 : (cell.Character != ' ' && cell.Character != '\0');

                        if (hasChar)
                        {
                            var cb = cell.IsDefaultBackground ? themeBg : new SKColor(cell.Background.R, cell.Background.G, cell.Background.B, alpha);
                            var cf = cell.IsDefaultForeground ? themeFg : new SKColor(cell.Foreground.R, cell.Foreground.G, cell.Foreground.B, alpha);
                            var fg = cell.IsInverse ? cb : cf;

                            // If it's a wide char or complex text, draw it individually
                            // We don't batch wide chars with standard chars to ensure spacing is correct
                            if (cell.IsWide || !string.IsNullOrEmpty(cell.Text))
                            {
                                string text = cell.Text ?? cell.Character.ToString();

                                // FONT FALLBACK LOGIC
                                // Check if primary font supports this character/grapheme
                                int codepoint = 0;
                                try
                                {
                                    if (text.Length == 1) codepoint = text[0];
                                    else if (text.Length == 2 && char.IsSurrogatePair(text[0], text[1])) codepoint = char.ConvertToUtf32(text, 0);
                                    else { /* Invalid string, skip rendering */ continue; }
                                }
                                catch { continue; } // Extra safety

                                if (!tf.ContainsGlyph(codepoint))
                                {
                                    // Try to find a fallback font
                                    using var fallbackTf = SKFontManager.Default.MatchCharacter(codepoint);
                                    if (fallbackTf != null)
                                    {
                                        using var fallbackFont = new SKFont(fallbackTf, (float)_fontSize);
                                        fallbackFont.Edging = SKFontEdging.Antialias;
                                        fgPaint.Color = fg;
                                        canvas.DrawText(text, (float)(Math.Round((c * _charWidth + paddingLeft) * _renderScaling) / _renderScaling), baselineY, fallbackFont, fgPaint);
                                    }
                                    else
                                    {
                                        // No fallback found, draw with default (will likely show box)
                                        fgPaint.Color = fg;
                                        canvas.DrawText(text, (float)(Math.Round((c * _charWidth + paddingLeft) * _renderScaling) / _renderScaling), baselineY, font, fgPaint);
                                    }
                                }
                                else
                                {
                                    fgPaint.Color = fg;
                                    canvas.DrawText(text, (float)(Math.Round((c * _charWidth + paddingLeft) * _renderScaling) / _renderScaling), baselineY, font, fgPaint);
                                }
                            }
                            else
                            {
                                // Batch simple chars for performance
                                int runStart = c;

                                // Avoid stackalloc in loop (CA2014)
                                int remaining = bufferCols - c;
                                Span<char> runBuffer = new char[remaining]; // Allocation is safer than stack overflow
                                int runLength = 0;

                                runBuffer[runLength++] = cell.Character;

                                int k = c + 1;
                                while (k < bufferCols)
                                {
                                    var next = _buffer.GetCell(k, r, _scrollOffset);
                                    if (next.IsWide || next.IsWideContinuation || !string.IsNullOrEmpty(next.Text)) break; // Stop at complex chars
                                    if (next.Character == ' ' || next.Character == '\0') break;

                                    var nb = next.IsDefaultBackground ? themeBg : new SKColor(next.Background.R, next.Background.G, next.Background.B, alpha);
                                    var nf = next.IsDefaultForeground ? themeFg : new SKColor(next.Foreground.R, next.Foreground.G, next.Foreground.B, alpha);
                                    if ((next.IsInverse ? nb : nf) != fg) break;

                                    runBuffer[runLength++] = next.Character;
                                    k++;
                                }

                                fgPaint.Color = fg;
                                // Create string directly from span (allocates string only, no intermediate arrays)
                                canvas.DrawText(new string(runBuffer.Slice(0, runLength)), (float)(Math.Round((runStart * _charWidth + paddingLeft) * _renderScaling) / _renderScaling), baselineY, font, fgPaint);
                                dirtyCells += (k - runStart);
                                c = k - 1;
                            }
                        }
                    }
                }

                // Cursor
                if (!_hideCursor)
                {
                    int visualCursorRow = _buffer.GetVisualCursorRowInternal(_scrollOffset);
                    if (visualCursorRow >= 0 && visualCursorRow < bufferRows)
                    {
                        float x1 = (float)(Math.Round((_buffer.InternalCursorCol * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                        float x2 = (float)(Math.Round(((_buffer.InternalCursorCol + 1) * _charWidth + paddingLeft) * _renderScaling) / _renderScaling);
                        float cy = (float)(Math.Round((visualCursorRow * _charHeight + _charHeight - 2 + paddingTop) * _renderScaling) / _renderScaling);
                        canvas.DrawRect(x1, cy, x2 - x1, 2, new SKPaint { Color = new SKColor(255, 255, 255, alpha) });
                    }
                }

                // Currently every render is a full redraw in this custom operation
                RendererStatistics.RecordFrame(fullRedraw: true, dirtyCells: dirtyCells);
            }
            finally
            {
                _buffer.Lock.ExitReadLock();
            }
        }
    }
}
