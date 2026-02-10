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
        private readonly System.Collections.Generic.LinkedList<Guid> _lruOrder = new();
        private const int MaxImages = 200; // Bounded cache to prevent OOM
        private readonly object _lock = new();

        private ImageRegistry() { }

        public Guid RegisterImage(SKBitmap bitmap)
        {
            lock (_lock)
            {
                if (_images.Count >= MaxImages)
                {
                    // Evict oldest (least recently used or simply oldest)
                    var oldest = _lruOrder.First;
                    if (oldest != null)
                    {
                        if (_images.TryRemove(oldest.Value, out var oldBitmap))
                        {
                            oldBitmap.Dispose();
                        }
                        _lruOrder.RemoveFirst();
                    }
                }

                var id = Guid.NewGuid();
                _images[id] = bitmap;
                _lruOrder.AddLast(id);
                return id;
            }
        }

        public SKBitmap? GetImage(Guid id)
        {
            lock (_lock)
            {
                if (_images.TryGetValue(id, out var bitmap))
                {
                    // Move to end of LRU (most recently used)
                    _lruOrder.Remove(id);
                    _lruOrder.AddLast(id);
                    return bitmap;
                }
                return null;
            }
        }

        public void RemoveImage(Guid id)
        {
            lock (_lock)
            {
                if (_images.TryRemove(id, out var bitmap))
                {
                    bitmap.Dispose();
                    _lruOrder.Remove(id);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                foreach (var bitmap in _images.Values)
                {
                    bitmap.Dispose();
                }
                _images.Clear();
                _lruOrder.Clear();
            }
        }
    }
}
