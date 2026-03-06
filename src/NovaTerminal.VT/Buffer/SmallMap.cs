using System;
using System.Collections.Generic;

namespace NovaTerminal.Core.Storage
{
    /// <summary>
    /// Memory-optimized map for few entries (<= 8).
    /// Used to reduce memory overhead from per-row dictionaries for extended text and hyperlinks.
    ///
    /// Rationale: 
    /// Standard Dictionary<int, T> has significant object and array overhead.
    /// Most terminal rows have 0, 1, or very few extended graphemes or hyperlinks.
    /// SmallMap uses a simple array for up to 8 entries, avoiding Dictionary overhead
    /// unless the complexity of the row justifies it.
    /// </summary>
    public sealed class SmallMap<T> where T : class
    {
        private const int MaxSmallCount = 8;

        private struct Entry
        {
            public int Key;
            public T Value;
        }

        private Entry[]? _entries;
        private Dictionary<int, T>? _dictionary;
        private int _count;

        public int Count => _dictionary?.Count ?? _count;

        /// <summary>
        /// Iterates over all key-value pairs without allocation.
        /// </summary>
        public void ForEach(System.Action<int, T> action)
        {
            if (_dictionary != null)
            {
                foreach (var kvp in _dictionary) action(kvp.Key, kvp.Value);
                return;
            }
            if (_entries != null)
            {
                for (int i = 0; i < _count; i++)
                    action(_entries[i].Key, _entries[i].Value);
            }
        }

        /// <summary>
        /// Attempts to get the value associated with the specified key.
        /// </summary>
        public bool TryGet(int key, out T? value)
        {
            if (_dictionary != null)
            {
                return _dictionary.TryGetValue(key, out value);
            }

            if (_count > 0 && _entries != null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i].Key == key)
                    {
                        value = _entries[i].Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Sets the value for the specified key.
        /// Upgrades to Dictionary if count exceeds 8.
        /// </summary>
        public void Set(int key, T value)
        {
            if (_dictionary != null)
            {
                _dictionary[key] = value;
                return;
            }

            // Check existing
            if (_count > 0 && _entries != null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i].Key == key)
                    {
                        _entries[i].Value = value;
                        return;
                    }
                }
            }

            if (_count < MaxSmallCount)
            {
                _entries ??= new Entry[MaxSmallCount];
                _entries[_count++] = new Entry { Key = key, Value = value };
            }
            else
            {
                // Upgrade to dictionary
                _dictionary = new Dictionary<int, T>(_count + 1);
                if (_entries != null)
                {
                    for (int i = 0; i < _count; i++)
                    {
                        _dictionary[_entries[i].Key] = _entries[i].Value;
                    }
                }
                _dictionary[key] = value;
                _entries = null;
                _count = 0;
            }
        }

        /// <summary>
        /// Removes the value with the specified key.
        /// </summary>
        public void Remove(int key)
        {
            if (_dictionary != null)
            {
                _dictionary.Remove(key);
                return;
            }

            if (_count > 0 && _entries != null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i].Key == key)
                    {
                        // Shift left to maintain contiguous array
                        for (int j = i; j < _count - 1; j++)
                        {
                            _entries[j] = _entries[j + 1];
                        }
                        _entries[_count - 1] = default; // Clear reference for GC
                        _count--;
                        return;
                    }
                }
            }
        }
    }
}
