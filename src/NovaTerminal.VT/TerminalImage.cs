using System;

namespace NovaTerminal.VT
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

        /// <summary>
        /// True when the image was placed while the alternate screen was active.
        /// Alt-screen images use viewport-relative <see cref="CellY"/>; main-screen
        /// images use absolute rows (scrollback + viewport). Set by
        /// <c>TerminalBuffer.AddImage</c>.
        /// </summary>
        public bool IsAltScreenImage { get; set; }

        /// <summary>
        /// The kitty graphics image number (<c>i=</c>) this image was placed under, when it
        /// came through the kitty APC path. A new frame reusing the same number replaces the
        /// previous one (see <c>TerminalBuffer.AddKittyFrame</c>) instead of stacking — this is
        /// what keeps a video-rate frame stream (e.g. terminal-browser) to one live image.
        /// Unsigned to cover the full 32-bit protocol range. Null for non-kitty images
        /// (iTerm2 OSC 1337, sixel).
        /// </summary>
        public uint? KittyImageId { get; set; }

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
