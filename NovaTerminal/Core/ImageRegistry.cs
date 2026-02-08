using System;
using System.Collections.Concurrent;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class ImageRegistry
    {
        private static readonly Lazy<ImageRegistry> _instance = new(() => new ImageRegistry());
        public static ImageRegistry Instance => _instance.Value;

        private readonly ConcurrentDictionary<Guid, SKBitmap> _images = new();

        private ImageRegistry() { }

        public Guid RegisterImage(SKBitmap bitmap)
        {
            var id = Guid.NewGuid();
            _images[id] = bitmap;
            return id;
        }

        public SKBitmap? GetImage(Guid id)
        {
            return _images.TryGetValue(id, out var bitmap) ? bitmap : null;
        }

        public void RemoveImage(Guid id)
        {
            if (_images.TryRemove(id, out var bitmap))
            {
                bitmap.Dispose();
            }
        }

        public void Clear()
        {
            foreach (var bitmap in _images.Values)
            {
                bitmap.Dispose();
            }
            _images.Clear();
        }
    }
}
