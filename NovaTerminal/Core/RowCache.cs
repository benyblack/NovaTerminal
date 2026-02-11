using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class RowImageCache : IDisposable
    {
        private readonly Dictionary<(int RowIndex, uint Revision), SKPicture> _cache = new();
        private readonly List<(int RowIndex, uint Revision)> _lru = new();
        private const int MaxEntries = 1000; // Pictures are very small, we can keep more

        public SKPicture? Get(int rowIndex, uint revision)
        {
            if (_cache.TryGetValue((rowIndex, revision), out var picture))
            {
                // Update LRU: move to end
                _lru.Remove((rowIndex, revision));
                _lru.Add((rowIndex, revision));
                return picture;
            }
            return null;
        }

        public void Add(int rowIndex, uint revision, SKPicture picture)
        {
            if (_cache.Count >= MaxEntries)
            {
                // Evict oldest
                var oldest = _lru[0];
                if (_cache.Remove(oldest, out var oldPicture))
                {
                    oldPicture.Dispose();
                }
                _lru.RemoveAt(0);
            }

            _cache[(rowIndex, revision)] = picture;
            _lru.Add((rowIndex, revision));
        }

        public void Clear()
        {
            foreach (var img in _cache.Values)
            {
                img.Dispose();
            }
            _cache.Clear();
            _lru.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
