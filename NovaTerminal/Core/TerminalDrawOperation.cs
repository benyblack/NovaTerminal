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
            SKFont? skFont = null)
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
                var themeBg = _buffer.Theme.Background;
                canvas.Clear(new SKColor(themeBg.R, themeBg.G, themeBg.B, themeBg.A));

                using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill };
                using var fgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                
                // Use cached font or create fallback (fallback is slow but correct)
                SKFont? font = _skFont;
                bool disposeFont = false;
                if (font == null)
                {
                    var tf = _skTypeface ?? SKTypeface.FromFamilyName(_typeface.FontFamily.Name);
                    font = new SKFont(tf, (float)_fontSize);
                    disposeFont = true;
                }
                
                using var selectionPaint = new SKPaint { Color = new SKColor(51, 153, 255, 100) };
                using var matchPaint = new SKPaint { Color = new SKColor(255, 255, 0, 100) }; // Yellow
                using var activeMatchPaint = new SKPaint { Color = new SKColor(255, 128, 0, 150) }; // Orange

                int totalLines = _buffer.TotalLines;
                int displayStart = Math.Max(0, totalLines - _buffer.Rows - _scrollOffset);

                char[] charBuffer = new char[_buffer.Cols];

                for (int r = 0; r < _buffer.Rows; r++)
                {
                    float y = (float)(r * _charHeight);
                    float baselineY = y + (float)_baselineOffset;
                    int absRow = displayStart + r;

                    // Pass 1: Backgrounds
                    for (int c = 0; c < _buffer.Cols; c++)
                    {
                        var cell = _buffer.GetCell(c, r, _scrollOffset);
                        var cellBg = cell.IsDefaultBackground ? themeBg : cell.Background;
                        var cellFg = cell.IsDefaultForeground ? _buffer.Theme.Foreground : cell.Foreground;
                        if (cell.IsInverse) { var tmp = cellBg; cellBg = cellFg; cellFg = tmp; }

                        if (cellBg != themeBg)
                        {
                            var bg = cellBg;
                            int runStart = c;
                            int k = c + 1;
                            while(k < _buffer.Cols)
                            {
                                var nextCell = _buffer.GetCell(k, r, _scrollOffset);
                                var nextBg = nextCell.IsDefaultBackground ? themeBg : nextCell.Background;
                                var nextFg = nextCell.IsDefaultForeground ? _buffer.Theme.Foreground : nextCell.Foreground;
                                if (nextCell.IsInverse) nextBg = nextFg; // Simplified for comparison logic
                                if (nextBg != bg) break;
                                k++;
                            }
                            bgPaint.Color = new SKColor(bg.R, bg.G, bg.B, bg.A);
                            float x = (float)(runStart * _charWidth);
                            float w = (float)((k - runStart) * _charWidth);
                            canvas.DrawRect(x, y, w, (float)_charHeight, bgPaint);
                            c = k - 1;
                        }
                    }

                    // Pass 1.5: Selection
                    if (_selection.IsActive)
                    {
                        var (isSelected, colStart, colEnd) = _selection.GetSelectionRangeForRow(absRow, _buffer.Cols);
                        if (isSelected)
                        {
                            canvas.DrawRect((float)(colStart * _charWidth), y, (float)((colEnd - colStart + 1) * _charWidth), (float)_charHeight, selectionPaint);
                        }
                    }

                    // Pass 1.6: Search Matches
                    if (_searchMatches != null && _searchMatches.Count > 0)
                    {
                        for (int i = 0; i < _searchMatches.Count; i++)
                        {
                            var match = _searchMatches[i];
                            if (match.AbsRow == absRow)
                            {
                                var paint = (i == _activeSearchIndex) ? activeMatchPaint : matchPaint;
                                float x = (float)(match.StartCol * _charWidth);
                                float w = (float)((match.EndCol - match.StartCol + 1) * _charWidth);
                                canvas.DrawRect(x, y, w, (float)_charHeight, paint);
                            }
                        }
                    }

                    // Pass 2: Foreground (Text)
                    for (int c = 0; c < _buffer.Cols; c++)
                    {
                        var cell = _buffer.GetCell(c, r, _scrollOffset);
                        if (cell.Character != ' ' && cell.Character != '\0')
                        {
                            var cellBg = cell.IsDefaultBackground ? themeBg : cell.Background;
                            var cellFg = cell.IsDefaultForeground ? _buffer.Theme.Foreground : cell.Foreground;
                            var fg = cell.IsInverse ? cellBg : cellFg;
                            
                            bool bold = cell.IsBold;
                            int runStart = c;
                            charBuffer[0] = cell.Character;
                            int count = 1;
                             
                            int k = c + 1;
                            while(k < _buffer.Cols && count < charBuffer.Length)
                            {
                                var next = _buffer.GetCell(k, r, _scrollOffset);
                                var nBg = next.IsDefaultBackground ? themeBg : next.Background;
                                var nFg = next.IsDefaultForeground ? _buffer.Theme.Foreground : next.Foreground;
                                var nextFgMapped = next.IsInverse ? nBg : nFg;

                                if (nextFgMapped != fg || next.IsBold != bold || next.Character == '\0' || next.Character == ' ') break;
                                 
                                charBuffer[count++] = next.Character;
                                k++;
                            }
                             
                            fgPaint.Color = new SKColor(fg.R, fg.G, fg.B, fg.A);
                            float x = (float)(runStart * _charWidth);
                            canvas.DrawText(new string(charBuffer, 0, count), x, baselineY, font, fgPaint);
                            c = k - 1;
                        }
                    }
                }

                int visualCursorRow = _buffer.GetVisualCursorRow(_scrollOffset);
                if (visualCursorRow >= 0 && visualCursorRow < _buffer.Rows)
                {
                    canvas.DrawRect((float)(_buffer.CursorCol * _charWidth), (float)(visualCursorRow * _charHeight + _charHeight - 2), (float)_charWidth, 2, new SKPaint { Color = SKColors.White });
                }

                if (disposeFont) font.Dispose();
            }
            finally
            {
                _buffer.Lock.ExitReadLock();
            }
        }
    }
}
