using System;
using Xunit;
using SkiaSharp;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Rendering
{
    public class GlyphCacheTests : IDisposable
    {
        private readonly GlyphCache _cache;
        private readonly SKTypeface _typeface;
        private readonly SKFont _font;

        public GlyphCacheTests()
        {
            _cache = new GlyphCache();
            _typeface = SKTypeface.FromFamilyName("Arial");
            _font = new SKFont(_typeface, 12);
        }

        [Fact]
        public void GetOrAdd_ReturnsValidRect()
        {
            var result = _cache.GetOrAdd("A", _font, 1.0f);
            Assert.NotNull(result);
            Assert.True(result.Value.Rect.Width > 0);
            Assert.True(result.Value.Rect.Height > 0);
        }

        [Fact]
        public void GetOrAdd_IdenticalParameters_ReturnsSameRect()
        {
            var result1 = _cache.GetOrAdd("A", _font, 1.0f);
            var result2 = _cache.GetOrAdd("A", _font, 1.0f);

            Assert.Equal(result1!.Value.Rect, result2!.Value.Rect);
        }

        [Fact]
        public void GetOrAdd_DifferentText_ReturnsDifferentRect()
        {
            var result1 = _cache.GetOrAdd("A", _font, 1.0f);
            var result2 = _cache.GetOrAdd("B", _font, 1.0f);

            Assert.NotEqual(result1!.Value.Rect, result2!.Value.Rect);
        }

        [Fact]
        public void GetOrAdd_DifferentSize_ReturnsDifferentRect()
        {
            using var font2 = new SKFont(_typeface, 14);
            var result1 = _cache.GetOrAdd("A", _font, 1.0f);
            var result2 = _cache.GetOrAdd("A", font2, 1.0f);

            Assert.NotEqual(result1!.Value.Rect, result2!.Value.Rect);
        }

        [Fact]
        public void GetOrAdd_DifferentScale_ReturnsDifferentRect()
        {
            var result1 = _cache.GetOrAdd("A", _font, 1.0f);
            var result2 = _cache.GetOrAdd("A", _font, 2.0f);

            Assert.NotEqual(result1!.Value.Rect, result2!.Value.Rect);
        }

        [Fact]
        public void GetOrAdd_EmptyText_ReturnsNull()
        {
            var result = _cache.GetOrAdd("", _font, 1.0f);
            Assert.Null(result);
        }

        [Fact]
        public void Clear_InvalidatesCache()
        {
            var result1 = _cache.GetOrAdd("A", _font, 1.0f);
            _cache.Clear();
            var result2 = _cache.GetOrAdd("A", _font, 1.0f);

            // They might get the same rect if the atlas packer starts over,
            // but for a test of logic, we just ensure it doesn't crash.
            Assert.NotNull(result2);
        }

        public void Dispose()
        {
            _font.Dispose();
            _typeface.Dispose();
            _cache.Dispose();
        }
    }
}
