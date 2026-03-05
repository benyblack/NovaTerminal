using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public enum AtlasType
    {
        Alpha8, // For monochromatic text (grayscale/alpha)
        Color   // For color emojis (RGBA)
    }

    public struct AtlasGlyph
    {
        public SKImage Image;
        public SKRect Rect;
        public AtlasType Type;
    }

    public class GlyphAtlas : IDisposable
    {
        private const int AtlasSize = 1024;
        private readonly SKSurface _alphaSurface;
        private readonly SKSurface _colorSurface;

        private int _alphaShelfY = 0;
        private int _alphaNextX = 0;
        private int _alphaMaxShelfHeight = 0;

        private int _colorShelfY = 0;
        private int _colorNextX = 0;
        private int _colorMaxShelfHeight = 0;

        public GlyphAtlas()
        {
            // Both surfaces use Rgba8888 — required for DrawAtlas with SKBlendMode.Modulate,
            // which multiplies texture RGB (must be white=255) by vertex color to tint glyphs.
            // Alpha8 would give RGB=(0,0,0) → all glyphs render black.
            // 1024² is a 4× reduction from the original 2048², saving 12MB per atlas.
            _alphaSurface = SKSurface.Create(new SKImageInfo(AtlasSize, AtlasSize, SKColorType.Rgba8888));
            _colorSurface = SKSurface.Create(new SKImageInfo(AtlasSize, AtlasSize, SKColorType.Rgba8888));

            // Clear surfaces
            _alphaSurface.Canvas.Clear(SKColors.Transparent);
            _colorSurface.Canvas.Clear(SKColors.Transparent);
        }

        public SKRect? Pack(int width, int height, AtlasType type)
        {
            if (type == AtlasType.Alpha8)
                return PackOnSurface(ref _alphaNextX, ref _alphaShelfY, ref _alphaMaxShelfHeight, width, height);
            else
                return PackOnSurface(ref _colorNextX, ref _colorShelfY, ref _colorMaxShelfHeight, width, height);
        }

        private SKRect? PackOnSurface(ref int nextX, ref int shelfY, ref int maxShelfHeight, int w, int h)
        {
            // Add 1px padding to avoid bleeding
            int pw = w + 1;
            int ph = h + 1;

            if (nextX + pw > AtlasSize)
            {
                // Move to next shelf
                shelfY += maxShelfHeight;
                nextX = 0;
                maxShelfHeight = 0;
            }

            if (shelfY + ph > AtlasSize)
            {
                // Atlas Full - we need a reset strategy (for now just return null)
                return null;
            }

            var rect = SKRect.Create(nextX, shelfY, w, h);
            nextX += pw;
            if (ph > maxShelfHeight) maxShelfHeight = ph;

            return rect;
        }

        public void DrawGlyph(SKRect rect, Action<SKCanvas> drawAction, AtlasType type)
        {
            var surface = type == AtlasType.Alpha8 ? _alphaSurface : _colorSurface;
            var canvas = surface.Canvas;
            canvas.Save();
            canvas.ClipRect(rect);
            canvas.Translate(rect.Left, rect.Top);
            drawAction(canvas);
            canvas.Restore();
        }

        public SKImage GenerateAlphaImage() => _alphaSurface.Snapshot();
        public SKImage GenerateColorImage() => _colorSurface.Snapshot();

        public void Reset()
        {
            _alphaShelfY = 0;
            _alphaNextX = 0;
            _alphaMaxShelfHeight = 0;
            _colorShelfY = 0;
            _colorNextX = 0;
            _colorMaxShelfHeight = 0;
            _alphaSurface.Canvas.Clear(SKColors.Transparent);
            _colorSurface.Canvas.Clear(SKColors.Transparent);
        }

        public void Dispose()
        {
            _alphaSurface.Dispose();
            _colorSurface.Dispose();
        }
    }
}
