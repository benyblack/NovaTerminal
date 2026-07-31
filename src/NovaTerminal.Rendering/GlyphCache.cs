using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace NovaTerminal.Rendering
{
    /// <summary>
    /// A rasterized glyph in the atlas, and where to put it.
    /// </summary>
    /// <param name="Rect">The glyph's pixels within its atlas surface.</param>
    /// <param name="Type">Which surface — see <see cref="GlyphCache.WantsColorGlyph"/>.</param>
    /// <param name="OffsetX">
    /// Device-pixel X of the sprite's left edge relative to the pen origin. Usually 0, negative for a
    /// glyph whose ink starts left of the origin.
    /// </param>
    /// <param name="OffsetY">
    /// Device-pixel Y of the sprite's top edge relative to the baseline, so normally negative.
    /// </param>
    /// <remarks>
    /// #172 item 2: the atlas used to pack every glyph into its <em>advance</em> box — advance width by
    /// font-wide ascent-to-descent — and the caller placed the sprite at (pen, baseline + ascent),
    /// which only works while ink stays inside that box. It frequently does not, so the offsets are
    /// now carried per glyph rather than assumed.
    /// </remarks>
    public readonly record struct GlyphSprite(SKRect Rect, AtlasType Type, int OffsetX, int OffsetY);

    public class GlyphCache : IDisposable
    {
        private class CacheEntry
        {
            public SKRect Rect;
            public AtlasType Type;
            public int OffsetX;
            public int OffsetY;
            public long LastUsed;
        }

        /// <remarks>
        /// Note the absence of a skew component. #172 reported that <c>Skew</c> was part of this key
        /// but never applied when rasterizing, and proposed setting <c>SkewX</c> on the raster font to
        /// match. That would have been wrong: synthetic italic is applied as a *canvas* transform at
        /// the draw site (<c>canvas.Skew(-0.22f, 0f)</c> in <c>TerminalDrawOperation</c>), so atlas
        /// glyphs are supposed to be upright and skewing them too would double the slant.
        ///
        /// The real defect was that <c>SkewX</c> is never set on any font in this codebase, so the key
        /// component was always 0 — dead weight in every hash and comparison. Removed rather than
        /// populated. If synthetic italic ever moves into the atlas (worth doing: it would stop the
        /// italic path having to flush the sprite batch), the skew has to come back into the key *and*
        /// the canvas transform has to go, and the ink-overhang fix becomes a prerequisite, since a
        /// slanted glyph overflows its advance width by construction.
        /// </remarks>
        private readonly struct GlyphKey : IEquatable<GlyphKey>
        {
            public readonly string Text;
            public readonly SKTypeface Typeface;
            public readonly float Size;
            public readonly float Scale;

            public GlyphKey(string text, SKTypeface typeface, float size, float scale)
            {
                Text = text;
                Typeface = typeface;
                Size = size;
                Scale = scale;
            }

            public bool Equals(GlyphKey other)
            {
                return Text == other.Text &&
                       Typeface == other.Typeface &&
                       Size.Equals(other.Size) &&
                       Scale.Equals(other.Scale);
            }

            public override bool Equals(object? obj) => obj is GlyphKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Text);
                hash.Add(Typeface);
                hash.Add(Size);
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

        public GlyphSprite? GetOrAdd(string text, SKFont font, float scale)
        {
            if (string.IsNullOrEmpty(text)) return null;

            lock (_lock)
            {
                var key = new GlyphKey(text, font.Typeface, font.Size, scale);

                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.LastUsed = ++_usageCounter;
                    return new GlyphSprite(entry.Rect, entry.Type, entry.OffsetX, entry.OffsetY);
                }

                var packed = RasterizeAndPack(key, out bool tooLargeForAtlas);
                if (packed == null)
                {
                    // A glyph bigger than the whole atlas will never fit, so evicting cannot help.
                    // Bailing out here matters more since #172 made the packed box the *ink* box,
                    // which can exceed the advance box: without this, one oversized glyph in the
                    // visible set would reset the atlas on every single frame, permanently.
                    if (tooLargeForAtlas) return null;

                    // Genuine overflow. Rather than wiping every cached glyph (the old behaviour,
                    // which forced the whole visible set to be re-rasterized next frame — #125),
                    // rebuild keeping the most-recently-used half so the hot working set stays
                    // warm. Fall back to a full wipe only if even that can't make room.
                    RebuildRetainingHotEntries();
                    packed = RasterizeAndPack(key, out _);
                    if (packed == null)
                    {
                        ClearInternal();
                        RendererStatistics.RecordGlyphAtlasReset();
                        packed = RasterizeAndPack(key, out _);
                        if (packed == null) return null;
                    }
                }

                var sprite = packed.Value;
                _entries[key] = new CacheEntry
                {
                    Rect = sprite.Rect,
                    Type = sprite.Type,
                    OffsetX = sprite.OffsetX,
                    OffsetY = sprite.OffsetY,
                    LastUsed = ++_usageCounter
                };

                _needsUpdate = true;
                return sprite;
            }
        }

        /// <summary>
        /// True when a grapheme should be rasterized into the RGBA colour atlas rather than the
        /// Alpha8 one.
        /// </summary>
        /// <remarks>
        /// The two atlases are not interchangeable, and getting this wrong is lossy in *both*
        /// directions, which is why the checks below are deliberate rather than generous:
        ///
        /// <list type="bullet">
        /// <item>Alpha8 glyphs are tinted with the cell's foreground colour at draw time, so routing an
        /// emoji here renders it as a flat monochrome silhouette — the #172 symptom.</item>
        /// <item>Colour glyphs are blitted as-is, so routing ordinary text here would make it ignore
        /// the foreground colour entirely and always paint as rasterized. That is *worse*, so "when
        /// unsure, pick colour" is not a safe default.</item>
        /// </list>
        ///
        /// The previous test was two codepoint ranges — <c>1F300-1FAFF</c> and <c>2600-27BF</c> — which
        /// silently missed whole classes: regional-indicator flags sit at <c>1F1E6-1F1FF</c>, *below*
        /// the first range; keycap sequences are ASCII + <c>FE0F</c> + <c>20E3</c>; and singletons like
        /// <c>2B50</c> (star) fall between the two.
        ///
        /// Three of the four checks are structural — an explicit emoji-presentation selector, a
        /// regional-indicator pair, a keycap combiner — and those are exact. The fourth is a range
        /// table for codepoints whose *default* presentation is emoji, and a table is inherently a
        /// maintenance burden: it is how the original bug happened. The principled alternative is to
        /// ask the typeface whether the glyph has colour layers (COLR/CBDT), which SkiaSharp does not
        /// expose; recorded on #172 rather than pretended away.
        /// </remarks>
        internal static bool WantsColorGlyph(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;

                // VARIATION SELECTOR-16: an explicit request for emoji presentation, whatever the base
                // character's default is. Covers keycaps and text-default pictographs that an
                // application asked to be rendered in colour.
                if (cp == 0xFE0F) return true;

                // COMBINING ENCLOSING KEYCAP — the tail of a keycap sequence such as "1️⃣".
                if (cp == 0x20E3) return true;

                // Regional indicators. A pair forms a flag; a lone one still renders as a letter tile
                // from the emoji font rather than as text.
                if (cp >= 0x1F1E6 && cp <= 0x1F1FF) return true;

                if (IsEmojiPresentationByDefault(cp)) return true;
            }

            return false;
        }

        /// Codepoints whose default presentation is emoji (Unicode Emoji_Presentation=Yes), as ranges.
        /// Kept narrow on purpose: text-default pictographs are excluded so they keep foreground
        /// tinting, and a <c>FE0F</c> selector promotes them when an application does want colour.
        private static bool IsEmojiPresentationByDefault(int cp)
        {
            return cp switch
            {
                // Miscellaneous Symbols and Pictographs, Emoticons, Transport, Supplemental Symbols
                // and Pictographs, Symbols and Pictographs Extended-A — plus the skin-tone modifiers
                // at 1F3FB-1F3FF, which live inside this span.
                >= 0x1F300 and <= 0x1FAFF => true,

                // Mahjong tile red dragon, playing card black joker.
                0x1F004 or 0x1F0CF => true,

                // Enclosed alphanumeric supplement pictographs that are emoji by default.
                0x1F18E => true,
                >= 0x1F191 and <= 0x1F19A => true,

                // Enclosed Ideographic Supplement, enumerated rather than taken as a block. Most of
                // 1F200-1F2FF is unassigned or non-emoji, and two members - 1F202 (squared katakana
                // SA) and 1F237 (squared month) - are Emoji_Presentation=No, i.e. text by default.
                // A block-wide match would send those to the colour atlas and cost them their
                // foreground tint, which is the failure mode this predicate exists to avoid.
                0x1F201 or 0x1F21A or 0x1F22F => true,
                >= 0x1F232 and <= 0x1F236 => true,
                >= 0x1F238 and <= 0x1F23A => true,
                0x1F250 or 0x1F251 => true,

                // Miscellaneous Symbols / Dingbats. Kept as the original broad range: it is the one
                // span the previous code got right, and narrowing it now would be a behaviour change
                // beyond the scope of this fix.
                >= 0x2600 and <= 0x27BF => true,

                // Watch, hourglass, and the media-control and clock pictographs that are emoji by
                // default while their block neighbours are text.
                0x231A or 0x231B => true,
                >= 0x23E9 and <= 0x23EC => true,
                0x23F0 or 0x23F3 => true,

                // Small squares that are emoji-default (their larger and outlined siblings are not).
                0x25FD or 0x25FE => true,

                // Black/white large squares, star, hollow circle — 2B50 is the singleton the old
                // two-range test fell straight through.
                0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,

                // Wavy dash, part alternation mark, circled/squared ideographs.
                0x3030 or 0x303D or 0x3297 or 0x3299 => true,

                _ => false,
            };
        }

        /// <summary>
        /// Measures, packs and rasterizes a glyph into the atlas. Returns null without touching
        /// <c>_entries</c> when it did not fit, so the caller can decide how to evict. Must be called
        /// under <c>_lock</c>.
        /// </summary>
        /// <param name="tooLargeForAtlas">
        /// True when the glyph is larger than an entire atlas surface and so can never fit, however
        /// much is evicted. Distinguished from ordinary overflow because the caller's response has to
        /// differ: evicting is pointless and resetting every frame is worse than not caching it.
        /// </param>
        private GlyphSprite? RasterizeAndPack(GlyphKey key, out bool tooLargeForAtlas)
        {
            tooLargeForAtlas = false;
            string text = key.Text;

            var type = WantsColorGlyph(text) ? AtlasType.Color : AtlasType.Alpha8;

            // Use physically scaled font for the atlas to ensure bit-perfect sharpness
            float physicalSize = key.Size * key.Scale;
            using var physFont = new SKFont(key.Typeface, physicalSize);
            physFont.Edging = SKFontEdging.Antialias;
            physFont.Hinting = SKFontHinting.Full;
            physFont.Subpixel = true;

            // #172 item 2: pack the *ink* bounds, not the advance box. The old box was
            // ceil(MeasureText) wide by ceil(descent - ascent) tall with the glyph drawn at
            // y = round(-ascent), which silently cropped anything reaching outside it — measured in
            // Cascadia Code at 14px, that is Ã Å Ñ Õ Ĩ Ũ losing the top row of their diacritic, ď its
            // rightmost column and ĥ its leftmost; at 1.5x scaling, 62 glyphs in Latin-1 + Latin
            // Extended-A. Ink bounds are exact by construction, and usually *smaller* than the advance
            // box vertically, so the atlas also packs denser.
            physFont.MeasureText(text, out SKRect bounds);

            int left;
            int top;
            int w;
            int h;
            if (bounds.IsEmpty)
            {
                // No ink at all (a space, or a glyph the typeface draws as nothing). A 1x1
                // transparent sprite blits to nothing, which is the correct result and far smaller
                // than the blank advance-width box this used to reserve.
                left = 0;
                top = 0;
                w = 1;
                h = 1;
            }
            else
            {
                left = (int)Math.Floor(bounds.Left);
                top = (int)Math.Floor(bounds.Top);
                w = Math.Max(1, (int)Math.Ceiling(bounds.Right) - left);
                h = Math.Max(1, (int)Math.Ceiling(bounds.Bottom) - top);
            }

            // Pack() adds a 1px gutter, so the usable maximum is one less than the surface.
            if (w >= GlyphAtlas.AtlasSize || h >= GlyphAtlas.AtlasSize)
            {
                tooLargeForAtlas = true;
                return null;
            }

            var rect = _atlas.Pack(w, h, type);
            if (rect == null) return null;

            _atlas.DrawGlyph(rect.Value, (canvas) =>
            {
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.White,
                };
                // Shift the pen so the ink's top-left lands on the sprite's top-left.
                canvas.DrawText(text, -left, -top, physFont, paint);
            }, type);

            return new GlyphSprite(rect.Value, type, left, top);
        }

        // Reset the atlas and re-pack only the most-recently-used half of the cached glyphs,
        // dropping the cold remainder. Bounds the overflow cost to ~half the working set instead
        // of re-rasterizing everything, while keeping hot glyphs resident. Must be called under _lock.
        private void RebuildRetainingHotEntries()
        {
            List<KeyValuePair<GlyphKey, CacheEntry>> kept = _entries
                .OrderByDescending(kv => kv.Value.LastUsed)
                .Take(Math.Max(1, _entries.Count / 2))
                .ToList();

            _atlas.Reset();
            _entries.Clear();

            foreach (var kv in kept)
            {
                var packed = RasterizeAndPack(kv.Key, out bool tooLarge);
                if (packed == null)
                {
                    // An oversized glyph can never be re-packed, but the rest of the working set
                    // still can, so skip it rather than abandoning the rebuild.
                    if (tooLarge) continue;
                    break; // ran out of room again — drop the rest (coldest first)
                }

                _entries[kv.Key] = new CacheEntry
                {
                    Rect = packed.Value.Rect,
                    Type = packed.Value.Type,
                    OffsetX = packed.Value.OffsetX,
                    OffsetY = packed.Value.OffsetY,
                    LastUsed = kv.Value.LastUsed
                };
            }

            if (_alphaSnapshot != null) _disposalQueue.Add(_alphaSnapshot);
            if (_colorSnapshot != null) _disposalQueue.Add(_colorSnapshot);
            _alphaSnapshot = null;
            _colorSnapshot = null;
            _needsUpdate = true;

            RendererStatistics.RecordGlyphAtlasReset();
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
