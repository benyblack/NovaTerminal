using System;

namespace NovaTerminal.Core
{
    public class TerminalImage
    {
        public Guid ImageId { get; }
        public int CellX { get; set; }
        public int CellY { get; set; }
        public int CellWidth { get; }
        public int CellHeight { get; }
        public int ZIndex { get; set; } = 0;

        public object ImageHandle { get; }

        public bool IsSticky { get; set; } = true;

        public TerminalImage(object imageHandle, int cellX, int cellY, int cellWidth, int cellHeight)
        {
            ImageId = Guid.NewGuid();
            ImageHandle = imageHandle ?? throw new ArgumentNullException(nameof(imageHandle));
            CellX = cellX;
            CellY = cellY;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
        }
    }
}
