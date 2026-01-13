using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
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

        public void SetBuffer(TerminalBuffer buffer)
        {
            if (_buffer != null) _buffer.OnInvalidate -= InvalidateVisual;
            _buffer = buffer;
            if (_buffer != null) _buffer.OnInvalidate += InvalidateBuffer;
            
            // Measure char size
            var testText = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
            _charWidth = testText.Width;
            _charHeight = testText.Height;
            
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

            context.FillRectangle(Brushes.Black, this.Bounds);

            // Optimization: Build one FormattedText per row? Or draw individual glyphs?
            // Individual glyphs with background rects is simplest for colors.
            // But FormattedText is heavy if created per char.
            // Better: Build lines.
            
            for (int r = 0; r < _buffer.Rows; r++)
            {
                double y = r * _charHeight;
                for (int c = 0; c < _buffer.Cols; c++)
                {
                    var cell = _buffer.GetCell(c, r);
                    double x = c * _charWidth;

                    // Draw Background if not black
                    if (cell.Background != Colors.Black)
                    {
                        var brush = new SolidColorBrush(cell.Background);
                        context.FillRectangle(brush, new Rect(x, y, _charWidth, _charHeight));
                    }

                    // Draw Char
                    if (cell.Character != ' ' && cell.Character != '\0')
                    {
                        var brush = new SolidColorBrush(cell.Foreground);
                        var text = new FormattedText(
                            cell.Character.ToString(),
                            CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            _typeface,
                            _fontSize,
                            brush
                        );
                        // Center text vertically in the cell (if needed) or just align top
                        // Usually FormattedText draws top-aligned.
                        // Let's verify cursor rect. 
                        // If user says cursor is "above", maybe text is drawing TOO LOW?
                        // Or cursor rect is drawing TOO HIGH?
                        // Rect(x, y) is correct.
                        // Let's shift text UP slightly or push cursor DOWN?
                        // Actually, let's keep text at (x,y) and ensure cursor is fully covering the bottom?
                        
                        context.DrawText(text, new Point(x, y));
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
