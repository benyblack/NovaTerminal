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

        private readonly struct GlyphKey : IEquatable<GlyphKey>
        {
            public readonly string Text;
            public readonly SKTypeface Typeface;
            public readonly float Size;
            public readonly float Skew;
            public readonly float Scale;

            public GlyphKey(string text, SKTypeface typeface, float size, float skew, float scale)
            {
                Text = text;
                Typeface = typeface;
                Size = size;
                Skew = skew;
                Scale = scale;
            }

            public bool Equals(GlyphKey other)
            {
                return Text == other.Text &&
                       Typeface == other.Typeface &&
                       Size.Equals(other.Size) &&
                       Skew.Equals(other.Skew) &&
                       Scale.Equals(other.Scale);
            }

            public override bool Equals(object? obj) => obj is GlyphKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Text);
                hash.Add(Typeface);
                hash.Add(Size);
                hash.Add(Skew);
                hash.Add(Scale);
                return hash.ToHashCode();
            }
        }

        private readonly object _lock = new();
        private readonly List<SKImage> _disposalQueue = new();
        private readonly GlyphAtlas _atlas;
        private readonly Dictionary<GlyphKey, CacheEntry> _entries = new();
        private long _usageCounter = 0;

        // Color is NOT part of the key for Alpha8 as we color it during DrawAtlas.

        public int EntryCount => _entries.Count;
        public long AtlasByteSize => GlyphAtlas.AtlasSize * GlyphAtlas.AtlasSize * 4 * 2; // 2 surfaces, RGBA8888

        public GlyphCache()
        {
            _atlas = new GlyphAtlas();
        }

        public (SKRect Rect, AtlasType Type)? GetOrAdd(string text, SKFont font, float scale)
        {
            if (string.IsNullOrEmpty(text)) return null;

            lock (_lock)
            {
                var key = new GlyphKey(text, font.Typeface, font.Size, font.SkewX, scale);

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
                    ClearInternal();
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
        }

        private bool _needsUpdate = true;
        private SKImage? _alphaSnapshot;
        private SKImage? _colorSnapshot;

        public (SKImage Alpha, SKImage Color) GetAtlasImages()
        {
            lock (_lock)
            {
                if (_needsUpdate)
                {
                    if (_alphaSnapshot != null) _disposalQueue.Add(_alphaSnapshot);
                    if (_colorSnapshot != null) _disposalQueue.Add(_colorSnapshot);

                    _alphaSnapshot = _atlas.GenerateAlphaImage();
                    _colorSnapshot = _atlas.GenerateColorImage();
                    _needsUpdate = false;
                }
                return (_alphaSnapshot!, _colorSnapshot!);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                ClearInternal();
            }
        }

        private void ClearInternal()
        {
            _entries.Clear();
            _atlas.Reset();

            if (_alphaSnapshot != null) _disposalQueue.Add(_alphaSnapshot);
            if (_colorSnapshot != null) _disposalQueue.Add(_colorSnapshot);

            _alphaSnapshot = null;
            _colorSnapshot = null;
            _needsUpdate = true;
        }

        public void DrainDisposals()
        {
            SKImage[] toDispose;
            lock (_lock)
            {
                if (_disposalQueue.Count == 0) return;
                toDispose = _disposalQueue.ToArray();
                _disposalQueue.Clear();
            }

            foreach (var img in toDispose)
            {
                img.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _entries.Clear();
                _alphaSnapshot?.Dispose();
                _colorSnapshot?.Dispose();
                _alphaSnapshot = null;
                _colorSnapshot = null;

                foreach (var img in _disposalQueue) img.Dispose();
                _disposalQueue.Clear();

                _atlas.Dispose();
            }
        }
    }
}
