using System;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class TerminalImage
    {
        public Guid ImageId { get; }
        public int CellX { get; set; } // Logical column in buffer
        public int CellY { get; set; } // Logical row in buffer
        public int CellWidth { get; }  // Width in cells
        public int CellHeight { get; } // Height in cells
        public int ZIndex { get; set; } = 0; // 0 = background layer

        public SKBitmap Bitmap { get; }

        // If true, the image is "sticky" to the text and scrolls with it.
        public bool IsSticky { get; set; } = true;

        public TerminalImage(SKBitmap bitmap, int cellX, int cellY, int cellWidth, int cellHeight)
        {
            ImageId = Guid.NewGuid();
            Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
            CellX = cellX;
            CellY = cellY;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
        }
    }
}
