// RowImageCache.cs (O(1) LRU + deferred disposal; safe with Step 4)
// - Thread-safe
// - UI thread: call RequestClear() (Clear() maps to RequestClear() for backward compatibility)
// - Render thread: call DrainDisposalsAndApplyClearIfRequested() once per frame (e.g., top of DrawTerminal)
// - O(1) Get/Add via LinkedList + node map
// - Deferred disposal avoids use-after-dispose when UI thread invalidates cache mid-frame

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public sealed class RowImageCache : IDisposable
    {
        private readonly object _sync = new();

        private readonly LinkedList<Key> _lru = new();
        private readonly Dictionary<Key, Entry> _cache = new();

        private readonly List<SKPicture> _pendingDispose = new();



        private bool _clearRequested;

        public int MaxEntries { get; set; } = 150;

        private readonly struct Key : IEquatable<Key>
        {
            public readonly long RowId;
            public readonly uint Revision;

            public Key(long rowId, uint revision)
            {
                RowId = rowId;
                Revision = revision;
            }

            public bool Equals(Key other) => RowId == other.RowId && Revision == other.Revision;
            public override bool Equals(object? obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(RowId, Revision);
        }

        private sealed class Entry
        {
            public SKPicture Picture;
            public LinkedListNode<Key> Node;

            public Entry(SKPicture picture, LinkedListNode<Key> node)
            {
                Picture = picture;
                Node = node;
            }
        }

        public RowImageCache()
        {
        }
        public SKPicture? Get(long rowId, uint revision)
        {
            lock (_sync)
            {
                var key = new Key(rowId, revision);
                if (_cache.TryGetValue(key, out var entry))
                {
                    // Touch LRU: move to end (MRU)
                    _lru.Remove(entry.Node);
                    _lru.AddLast(entry.Node);
                    return entry.Picture;
                }
                return null;
            }
        }

        public bool TryGetLatestByRowId(long rowId, out SKPicture? picture, out uint revision)
        {
            lock (_sync)
            {
                picture = null;
                revision = 0;
                bool found = false;

                foreach (var kvp in _cache)
                {
                    if (kvp.Key.RowId != rowId)
                    {
                        continue;
                    }

                    if (!found || kvp.Key.Revision > revision)
                    {
                        found = true;
                        revision = kvp.Key.Revision;
                        picture = kvp.Value.Picture;
                    }
                }

                return found && picture != null;
            }
        }

        public void Add(long rowId, uint revision, SKPicture picture)
        {
            lock (_sync)
            {
                var key = new Key(rowId, revision);

                // Replace existing
                if (_cache.TryGetValue(key, out var existing))
                {
                    if (!ReferenceEquals(existing.Picture, picture))
                        _pendingDispose.Add(existing.Picture);

                    existing.Picture = picture;

                    // Touch LRU
                    _lru.Remove(existing.Node);
                    _lru.AddLast(existing.Node);
                    return;
                }

                // Evict if needed
                if (_cache.Count >= MaxEntries)
                {
                    var lruNode = _lru.First;
                    if (lruNode != null)
                    {
                        var lruKey = lruNode.Value;
                        _lru.RemoveFirst();

                        if (_cache.Remove(lruKey, out var evicted))
                        {
                            _pendingDispose.Add(evicted.Picture);
                        }
                    }
                }

                // Insert new
                var node = _lru.AddLast(key);
                _cache[key] = new Entry(picture, node);
            }
        }

        /// <summary>
        /// Backward-compatible API. Do NOT dispose synchronously; defer to render-thread drain.
        /// </summary>
        public void Clear() => RequestClear();

        /// <summary>
        /// Safe to call from any thread. Marks cache as invalid; disposal deferred to render thread.
        /// </summary>
        public void RequestClear()
        {
            lock (_sync)
            {
                _clearRequested = true;
            }
        }

        /// <summary>
        /// MUST be called from render thread at a safe boundary (once per frame before drawing).
        /// Applies pending clear and disposes any queued pictures.
        /// </summary>
        public void DrainDisposalsAndApplyClearIfRequested()
        {
            List<SKPicture>? toDispose = null;

            lock (_sync)
            {
                if (_clearRequested)
                {
                    _clearRequested = false;

                    // Move all cached pictures to pending dispose
                    foreach (var entry in _cache.Values)
                        _pendingDispose.Add(entry.Picture);

                    _cache.Clear();
                    _lru.Clear();
                }

                if (_pendingDispose.Count > 0)
                {
                    toDispose = new List<SKPicture>(_pendingDispose);
                    _pendingDispose.Clear();
                }
            }

            // Dispose outside lock
            if (toDispose != null)
            {
                foreach (var pic in toDispose)
                {
                    try { pic.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        public void Dispose()
        {
            // Best-effort: request clear then drain now.
            // Ideally Dispose is called when rendering is stopped.
            RequestClear();
            DrainDisposalsAndApplyClearIfRequested();
        }
    }
}
