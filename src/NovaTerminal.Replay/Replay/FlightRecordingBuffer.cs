using System;
using System.Collections.Generic;

namespace NovaTerminal.Replay
{
    /// <summary>Summary of a completed flight-recording export.</summary>
    public readonly struct FlightExportInfo
    {
        public FlightExportInfo(int eventCount, long firstEventMs, long lastEventMs, bool truncatedAtStart)
        {
            EventCount = eventCount;
            FirstEventMs = firstEventMs;
            LastEventMs = lastEventMs;
            TruncatedAtStart = truncatedAtStart;
        }

        /// <summary>Number of events written to the export file (output chunks + resizes).</summary>
        public int EventCount { get; }

        /// <summary>
        /// Timestamp of the first exported event, in milliseconds since flight recording
        /// was enabled. Timestamps inside the exported file are rebased so the first
        /// event is at t=0; this value locates the exported window on the session's
        /// recording timeline.
        /// </summary>
        public long FirstEventMs { get; }

        /// <summary>Timestamp of the last exported event, same timeline as <see cref="FirstEventMs"/>.</summary>
        public long LastEventMs { get; }

        /// <summary>True when the ring had already evicted older events; the export shows a suffix of the session.</summary>
        public bool TruncatedAtStart { get; }
    }

    /// <summary>
    /// Thread-safe bounded ring of recent raw PTY output and resize events — the
    /// "flight recorder" behind agent replay export. Retention is bounded by total
    /// payload bytes; when the budget is exceeded the oldest events are evicted.
    ///
    /// Privacy: this type has no input-event API by design. Agent-triggered exports
    /// must never contain typed input (passwords at prompts); replay rendering needs
    /// only output + resize. See docs/plans/2026-07-07-agent-host-a4-replay-design.md.
    ///
    /// The ring tracks the terminal geometry at the start of the retained window:
    /// when trimming evicts a resize event, the window-start geometry advances to that
    /// resize's dimensions, so an export is always self-consistent (the v2 header
    /// geometry equals the geometry in effect at the first retained event).
    /// </summary>
    public sealed class FlightRecordingBuffer
    {
        /// <summary>
        /// Fixed per-event bookkeeping charge against the byte budget, so payload-free
        /// events (resizes) cannot grow the ring without bound.
        /// </summary>
        public const int EntryOverheadBytes = 32;

        private readonly object _gate = new object();
        private readonly Queue<Entry> _entries = new Queue<Entry>();
        private readonly long _maxTotalBytes;
        private readonly Func<long> _clock;
        private readonly long _enabledAtMs;

        private long _retainedBytes;
        private int _windowStartCols;
        private int _windowStartRows;
        private bool _truncatedAtStart;

        /// <param name="maxTotalBytes">
        /// Retention budget: sum of payload bytes plus <see cref="EntryOverheadBytes"/>
        /// per event. Must be positive. The most recent event is always retained, even
        /// if it alone exceeds the budget.
        /// </param>
        /// <param name="initialCols">Terminal columns when recording starts.</param>
        /// <param name="initialRows">Terminal rows when recording starts.</param>
        /// <param name="clock">
        /// Monotonic millisecond clock (tests inject a fake). Defaults to
        /// <see cref="Environment.TickCount64"/>.
        /// </param>
        public FlightRecordingBuffer(long maxTotalBytes, int initialCols, int initialRows, Func<long>? clock = null)
        {
            if (maxTotalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), maxTotalBytes, "Retention budget must be positive.");
            if (initialCols <= 0) throw new ArgumentOutOfRangeException(nameof(initialCols), initialCols, "Columns must be positive.");
            if (initialRows <= 0) throw new ArgumentOutOfRangeException(nameof(initialRows), initialRows, "Rows must be positive.");

            _maxTotalBytes = maxTotalBytes;
            _windowStartCols = initialCols;
            _windowStartRows = initialRows;
            _clock = clock ?? (static () => Environment.TickCount64);
            _enabledAtMs = _clock();
        }

        /// <summary>Records a raw output chunk. Safe to call from the PTY read loop.</summary>
        public void RecordChunk(byte[] data, int length)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (length <= 0 || length > data.Length) return;

            byte[] copy = new byte[length];
            Array.Copy(data, copy, length);

            lock (_gate)
            {
                _entries.Enqueue(Entry.Chunk(NowMs(), copy));
                _retainedBytes += copy.Length + EntryOverheadBytes;
                TrimLocked();
            }
        }

        /// <summary>Records a terminal resize.</summary>
        public void RecordResize(int cols, int rows)
        {
            if (cols <= 0 || rows <= 0) return;

            lock (_gate)
            {
                _entries.Enqueue(Entry.Resize(NowMs(), cols, rows));
                _retainedBytes += EntryOverheadBytes;
                TrimLocked();
            }
        }

        /// <summary>
        /// Writes the retained window to <paramref name="filePath"/> as a standard
        /// replay v2 file via <see cref="PtyRecorder"/>. Event timestamps are rebased
        /// so the first retained event is at t=0. Concurrent writers keep recording;
        /// the export is a consistent point-in-time snapshot of the ring.
        /// </summary>
        public FlightExportInfo ExportTo(string filePath, string shell)
        {
            ArgumentNullException.ThrowIfNull(filePath);

            Entry[] entries;
            int cols;
            int rows;
            bool truncated;
            lock (_gate)
            {
                entries = _entries.ToArray();
                cols = _windowStartCols;
                rows = _windowStartRows;
                truncated = _truncatedAtStart;
            }

            using var recorder = new PtyRecorder(filePath, cols, rows, shell ?? string.Empty);

            if (entries.Length == 0)
            {
                return new FlightExportInfo(eventCount: 0, firstEventMs: 0, lastEventMs: 0, truncatedAtStart: truncated);
            }

            long baseMs = entries[0].TimeMs;
            foreach (Entry entry in entries)
            {
                long rebased = entry.TimeMs - baseMs;
                if (entry.Payload != null)
                {
                    recorder.RecordChunkAt(rebased, entry.Payload, entry.Payload.Length);
                }
                else
                {
                    recorder.RecordResizeAt(rebased, entry.Cols, entry.Rows);
                }
            }

            return new FlightExportInfo(
                eventCount: entries.Length,
                firstEventMs: entries[0].TimeMs,
                lastEventMs: entries[^1].TimeMs,
                truncatedAtStart: truncated);
        }

        /// <summary>Point-in-time view of the ring for tests and diagnostics.</summary>
        public FlightRecordingStats GetStats()
        {
            lock (_gate)
            {
                return new FlightRecordingStats(
                    eventCount: _entries.Count,
                    retainedBytes: _retainedBytes,
                    windowStartCols: _windowStartCols,
                    windowStartRows: _windowStartRows,
                    truncatedAtStart: _truncatedAtStart);
            }
        }

        private long NowMs()
        {
            long elapsed = _clock() - _enabledAtMs;
            return elapsed >= 0 ? elapsed : 0;
        }

        private void TrimLocked()
        {
            // Always retain the most recent event, even when it alone exceeds the
            // budget — an oversized final chunk must not silently empty the export.
            while (_entries.Count > 1 && _retainedBytes > _maxTotalBytes)
            {
                Entry evicted = _entries.Dequeue();
                _retainedBytes -= (evicted.Payload?.Length ?? 0) + EntryOverheadBytes;
                _truncatedAtStart = true;

                if (evicted.Payload == null)
                {
                    // Evicting a resize advances the geometry at the window start.
                    _windowStartCols = evicted.Cols;
                    _windowStartRows = evicted.Rows;
                }
            }
        }

        private readonly struct Entry
        {
            private Entry(long timeMs, byte[]? payload, int cols, int rows)
            {
                TimeMs = timeMs;
                Payload = payload;
                Cols = cols;
                Rows = rows;
            }

            public static Entry Chunk(long timeMs, byte[] payload) => new Entry(timeMs, payload, 0, 0);

            public static Entry Resize(long timeMs, int cols, int rows) => new Entry(timeMs, null, cols, rows);

            public long TimeMs { get; }

            /// <summary>Raw output bytes; null for resize entries.</summary>
            public byte[]? Payload { get; }

            public int Cols { get; }

            public int Rows { get; }
        }
    }

    /// <summary>Snapshot of <see cref="FlightRecordingBuffer"/> internals for tests and diagnostics.</summary>
    public readonly struct FlightRecordingStats
    {
        public FlightRecordingStats(int eventCount, long retainedBytes, int windowStartCols, int windowStartRows, bool truncatedAtStart)
        {
            EventCount = eventCount;
            RetainedBytes = retainedBytes;
            WindowStartCols = windowStartCols;
            WindowStartRows = windowStartRows;
            TruncatedAtStart = truncatedAtStart;
        }

        public int EventCount { get; }
        public long RetainedBytes { get; }
        public int WindowStartCols { get; }
        public int WindowStartRows { get; }
        public bool TruncatedAtStart { get; }
    }
}
