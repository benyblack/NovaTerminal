using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class GlyphCache : IDisposable
    {
        private class CacheEntry
        {
            public SKRect Rect;
            public AtlasType Type;
            public long LastUsed;
        }

        private readonly GlyphAtlas _atlas;
        private readonly Dictionary<string, CacheEntry> _entries = new();
        private long _usageCounter = 0;

        // Key format: "Text|FontName|Size|Skew|Scale"
        // Color is NOT part of the key for Alpha8 as we color it during DrawAtlas.

        public GlyphCache()
        {
            _atlas = new GlyphAtlas();
        }

        public (SKRect Rect, AtlasType Type)? GetOrAdd(string text, SKFont font, float scale)
        {
            string key = $"{text}|{font.Typeface.FamilyName}|{font.Size}|{font.SkewX}|{scale}";

            if (_entries.TryGetValue(key, out var entry))
            {
                entry.LastUsed = ++_usageCounter;
                return (entry.Rect, entry.Type);
            }

            bool isColor = false;
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if ((cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF))
                {
                    isColor = true;
                    break;
                }
            }

            var type = isColor ? AtlasType.Color : AtlasType.Alpha8;

            // Use physically scaled font for the atlas to ensure bit-perfect sharpness
            float physicalSize = font.Size * scale;
            using var physFont = new SKFont(font.Typeface, physicalSize);
            physFont.Edging = SKFontEdging.Antialias;
            physFont.Hinting = SKFontHinting.Full;
            physFont.Subpixel = true;

            // Measure the glyph at physical size
            float width = physFont.MeasureText(text);
            var metrics = physFont.Metrics;
            int h = (int)Math.Ceiling(metrics.Descent - metrics.Ascent);
            int w = (int)Math.Ceiling(width);

            if (w == 0) w = 1;

            var rect = _atlas.Pack(w, h, type);
            if (rect == null)
            {
                Clear();
                rect = _atlas.Pack(w, h, type);
                if (rect == null) return null;
            }

            _atlas.DrawGlyph(rect.Value, (canvas) =>
            {
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.White,
                };
                canvas.DrawText(text, 0, (float)Math.Round(-metrics.Ascent), physFont, paint);
            }, type);

            var newEntry = new CacheEntry
            {
                Rect = rect.Value,
                Type = type,
                LastUsed = ++_usageCounter
            };

            _entries[key] = newEntry;
            _needsUpdate = true;
            return (newEntry.Rect, newEntry.Type);
        }

        private bool _needsUpdate = true;
        private SKImage? _alphaSnapshot;
        private SKImage? _colorSnapshot;

        public (SKImage Alpha, SKImage Color) GetAtlasImages()
        {
            if (_needsUpdate)
            {
                _alphaSnapshot?.Dispose();
                _colorSnapshot?.Dispose();
                _alphaSnapshot = _atlas.GenerateAlphaImage();
                _colorSnapshot = _atlas.GenerateColorImage();
                _needsUpdate = false;
            }
            return (_alphaSnapshot!, _colorSnapshot!);
        }

        public void Clear()
        {
            _entries.Clear();
            _atlas.Reset();
            _alphaSnapshot?.Dispose();
            _colorSnapshot?.Dispose();
            _alphaSnapshot = null;
            _colorSnapshot = null;
            _needsUpdate = true;
        }

        public void Dispose()
        {
            _entries.Clear();
            _alphaSnapshot?.Dispose();
            _colorSnapshot?.Dispose();
            _atlas.Dispose();
        }
    }
}
