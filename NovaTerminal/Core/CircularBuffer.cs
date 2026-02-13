using System;
using System.Collections;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    /// <summary>
    /// A generic circular buffer with O(1) push and eviction.
    /// Used for terminal scrollback to replace List&lt;T&gt;.RemoveAt(0) which is O(n).
    /// </summary>
    /// <typeparam name="T">The type of elements in the buffer</typeparam>
    public class CircularBuffer<T> : IReadOnlyList<T>
    {
        private T[] _buffer;
        private int _head;     // Next write position
        private int _tail;     // Oldest item position (only meaningful when Count == Capacity)
        private int _count;
        private readonly int _capacity;

        public int Count => _count;
        public int Capacity => _capacity;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _capacity = capacity;
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        /// <summary>
        /// Adds an item to the buffer. If the buffer is full, the oldest item is evicted.
        /// </summary>
        public void Add(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;

            if (_count < _capacity)
            {
                _count++;
            }
            else
            {
                // Buffer is full, advance tail (evict oldest)
                _tail = (_tail + 1) % _capacity;
            }
        }

        /// <summary>
        /// Accesses an item by logical index (0 = oldest, Count-1 = newest).
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException($"Index {index} is out of range [0, {_count})");

                int physicalIndex = (_tail + index) % _capacity;
                return _buffer[physicalIndex];
            }
        }

        /// <summary>
        /// Clears all items from the buffer.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Returns the last (newest) item in the buffer.
        /// </summary>
        public T Last()
        {
            if (_count == 0)
                throw new InvalidOperationException("Buffer is empty");

            int lastIndex = (_head - 1 + _capacity) % _capacity;
            return _buffer[lastIndex];
        }
    }
}
