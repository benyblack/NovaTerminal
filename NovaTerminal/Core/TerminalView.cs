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
        // A robust list attempting to find ANY font with Powerline/Nerd symbols.
        private const string FontFamilyList = "MesloLGM Nerd Font, MesloLGS NF, Cascadia Code, Cascadia Mono, Fira Code, JetBrains Mono, Consolas, Segoe UI, Segoe Fluent Icons, Segoe UI Symbol, Monospace";
        private Typeface _typeface = new Typeface(FontFamilyList, FontStyle.Normal, FontWeight.Normal);
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

                // Pass 1: Backgrounds (Batched)
                for (int c = 0; c < _buffer.Cols; c++)
                {
                    var cell = _buffer.GetCell(c, r);
                    if (cell.Background != Colors.Black)
                    {
                        var bg = cell.Background;
                        int runStart = c;
                        
                        // Find run length
                        int k = c + 1;
                        while(k < _buffer.Cols)
                        {
                            if (_buffer.GetCell(k, r).Background != bg) break;
                            k++;
                        }
                        
                        // Draw single rect for the whole run
                        int runLength = k - c;
                        
                         // To avoid sub-pixel gaps between adjacent runs, we can round-up or add slight overlap.
                         // But simply drawing one big rect solves the internal gaps.
                        var bgBrush = new SolidColorBrush(bg);
                        context.FillRectangle(bgBrush, new Rect(runStart * _charWidth, y, runLength * _charWidth, _charHeight));
                        
                        c = k - 1;
                    }
                }

                // Pass 2: Foregrounds (Batched)
                for (int c = 0; c < _buffer.Cols; c++)
                {
                    var cell = _buffer.GetCell(c, r);
                    
                    if (cell.Character != ' ' && cell.Character != '\0')
                    {
                        Color fg = cell.Foreground;
                        
                        // Look ahead to find length of run with same FG
                        int runStart = c;
                        
                        // Initialize run
                        var glyphInfos = new List<GlyphInfo>();
                        
                        // Add first char
                        ushort glyph = _glyphTypeface.GetGlyph(cell.Character);
                        
                        if (glyph == 0)
                        {
                            // Fallback using FormattedText
                            var ft = new FormattedText(
                                cell.Character.ToString(),
                                CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                _typeface, 
                                _fontSize,
                                new SolidColorBrush(fg)
                            );
                            context.DrawText(ft, new Point(c * _charWidth, y));
                        }
                        else
                        {
                            // Normal glyph found
                            glyphInfos.Add(new GlyphInfo(glyph, 0, _charWidth));
                            
                            // Continue run
                            int k = c + 1;
                            while(k < _buffer.Cols)
                            {
                                var nextCell = _buffer.GetCell(k, r);
                                if (nextCell.Foreground != fg) break;
                                
                                var nextChar = nextCell.Character;
                                if (nextChar == ' ' || nextChar == '\0') 
                                {
                                    // Spaces break the visual run of *glyphs* usually? 
                                    // Actually, we can just skip drawing spaces but keep the run if color is same.
                                    // But simpler to just treat space as a glyph if the font has it, or break.
                                    // Ideally spaces are just empty. 
                                    // Let's break for safety or handle space glyph.
                                    // For now, simple logic: Break.
                                    break; 
                                }

                                var nextGlyph = _glyphTypeface.GetGlyph(nextChar);
                                
                                if (nextGlyph == 0) 
                                {
                                    break;
                                }
                                
                                glyphInfos.Add(new GlyphInfo(nextGlyph, k - c, _charWidth));
                                k++;
                            }

                            // Flush Run
                            if (glyphInfos.Count > 0)
                            {
                                var origin = new Point(runStart * _charWidth, baselineY);
                                var glyphRun = new GlyphRun(
                                    _glyphTypeface,
                                    _fontSize,
                                    characters: default, 
                                    glyphInfos: glyphInfos,
                                    baselineOrigin: origin
                                );
                                context.DrawGlyphRun(new SolidColorBrush(fg), glyphRun);
                            }
                            
                            // Advance c
                            c = k - 1;
                        }
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
