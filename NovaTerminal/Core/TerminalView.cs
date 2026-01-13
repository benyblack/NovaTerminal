using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NovaTerminal.Core
{
    public class TerminalView : Control
    {
        public TerminalView()
        {
            Focusable = true;
        }

        private TerminalBuffer? _buffer;
        private Typeface _typeface = new Typeface("Cascadia Code, Consolas, Monospace", FontStyle.Normal, FontWeight.Normal);
        private double _fontSize = 14;
        private double _charWidth;
        private double _charHeight;

        private IGlyphTypeface? _glyphTypeface;
        private double _baselineOffset;

        public void SetBuffer(TerminalBuffer buffer)
        {
            if (_buffer != null) _buffer.OnInvalidate -= InvalidateVisual;
            _buffer = buffer;
            if (_buffer != null) _buffer.OnInvalidate += InvalidateBuffer;
            
            // Measure char size and get GlyphTypeface
            var testText = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
            _charWidth = testText.Width;
            _charHeight = testText.Height;
            _baselineOffset = testText.Baseline;

            // Try to get IGlyphTypeface for low-level rendering
            _glyphTypeface = _typeface.GlyphTypeface;
            
            InvalidateVisual();
        }

        private void InvalidateBuffer()
        {
            Dispatcher.UIThread.InvokeAsync(InvalidateVisual);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_buffer != null && _charWidth > 0 && _charHeight > 0)
            {
                int cols = (int)(e.NewSize.Width / _charWidth);
                int rows = (int)(e.NewSize.Height / _charHeight);
                if (cols > 0 && rows > 0)
                {
                    _buffer.Resize(cols, rows);
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_buffer == null) return;

            // Clear background
            context.FillRectangle(Brushes.Black, this.Bounds);

            if (_glyphTypeface == null) return; // Fallback? Or just wait for load?

            // Batching Lists
            
            // Iterate rows
            for (int r = 0; r < _buffer.Rows; r++)
            {
                double y = r * _charHeight;
                double baselineY = y + _baselineOffset;

                for (int c = 0; c < _buffer.Cols; c++)
                {
                    var cell = _buffer.GetCell(c, r);
                    double x = c * _charWidth;

                    // 1. Draw Background (Immediate, or batch rects? Immediate is fine for now, rects are cheap)
                    if (cell.Background != Colors.Black)
                    {
                        var bgBrush = new SolidColorBrush(cell.Background); // Cache these brushes later!
                        context.FillRectangle(bgBrush, new Rect(x, y, _charWidth, _charHeight));
                    }

                    // 2. Collect Foreground Glyphs
                    // Naive: Draw one run per character (Wait, that's bad).
                    // Better: Collect run of SAME color.
                    
                    if (cell.Character != ' ' && cell.Character != '\0')
                    {
                        Color fg = cell.Foreground;
                        
                        // Look ahead to find length of run with same FG
                        int runStart = c;
                        
                        // Initialize run
                        var glyphInfos = new List<GlyphInfo>();
                        
                        // Add first char
                        var glyph = _glyphTypeface.GetGlyph(cell.Character);
                         // GlyphInfo(ushort glyphIndex, int cluster, double xAdvance)
                         // Cluster map 1:1 for now
                        glyphInfos.Add(new GlyphInfo(glyph, 0, _charWidth));
                        
                        // Continue run
                        int k = c + 1;
                        while(k < _buffer.Cols)
                        {
                            var nextCell = _buffer.GetCell(k, r);
                            if (nextCell.Foreground != fg) break;
                            
                            var nextChar = nextCell.Character;
                            var nextGlyph = _glyphTypeface.GetGlyph(nextChar);
                            
                            glyphInfos.Add(new GlyphInfo(nextGlyph, k - c, _charWidth)); // Cluster relative to run start? Or global? usually relative to text start if provided. If no text provided, maybe irrelevant.
                            k++;
                        }
                        
                        // Create Run
                        var origin = new Point(runStart * _charWidth, baselineY);
                        
                        // public GlyphRun(IGlyphTypeface glyphTypeface, double fontRenderingEmSize, ReadOnlyMemory<char> characters, IReadOnlyList<GlyphInfo> glyphInfos, Point? baselineOrigin, int bidiLevel)
                        var glyphRun = new GlyphRun(
                            _glyphTypeface,
                            _fontSize,
                            characters: default, 
                            glyphInfos: glyphInfos,
                            baselineOrigin: origin
                        );
                        
                        context.DrawGlyphRun(new SolidColorBrush(fg), glyphRun);
                        
                        // Advance c
                        c = k - 1; // loop increment will do c++
                    }
                }
            }

            // Draw Cursor
            double cursorX = _buffer.CursorCol * _charWidth;
            int visualCursorRow = _buffer.GetVisualCursorRow();
            
            if (visualCursorRow >= 0 && visualCursorRow < _buffer.Rows)
            {
                double cursorY = visualCursorRow * _charHeight;
                context.FillRectangle(Brushes.White, new Rect(cursorX, cursorY + _charHeight - 2, _charWidth, 2)); 
            }
        }
    }
}
