using System;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class TerminalImage
    {
        public Guid Id { get; } = Guid.NewGuid();
        public SKBitmap Bitmap { get; }

        // Logical position in the buffer
        public int StartRow { get; set; } // Absolute row in buffer
        public int StartCol { get; set; }

        // Display dimensions in terminal cells
        public int Rows { get; }
        public int Cols { get; }

        // If true, the image is "sticky" to the text and scrolls with it.
        // If false, it's pinned to the viewport (less common for Sixel/iTerm2).
        public bool IsSticky { get; set; } = true;

        public TerminalImage(SKBitmap bitmap, int startRow, int startCol, int rows, int cols)
        {
            Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
            StartRow = startRow;
            StartCol = startCol;
            Rows = rows;
            Cols = cols;
        }
    }
}
