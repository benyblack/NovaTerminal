using NovaTerminal.VT;
using NovaTerminal.Replay;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests
{
    /// <summary>
    /// Flight recorder core (agent-host A4 slice 1): bounded ring semantics,
    /// explicit-timestamp recorder overloads, and the export round-trip
    /// acceptance criterion from docs/plans/2026-07-07-agent-host-a4-replay-design.md.
    /// </summary>
    public class FlightRecordingBufferTests
    {
        private sealed class FakeClock
        {
            public long NowMs;
            public long Read() => NowMs;
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        private static void RecordChunk(FlightRecordingBuffer ring, string text)
        {
            byte[] bytes = Utf8(text);
            ring.RecordChunk(bytes, bytes.Length);
        }

        // ---------------------------------------------------------------
        // Ring semantics
        // ---------------------------------------------------------------

        [Fact]
        public void RecordChunk_OverBudget_EvictsOldestAndReportsTruncation()
        {
            // Budget fits two 100-byte chunks (plus overhead) but not three.
            long budget = 2 * (100 + FlightRecordingBuffer.EntryOverheadBytes) + 10;
            var ring = new FlightRecordingBuffer(budget, 80, 24, clock: static () => 0);

            ring.RecordChunk(new byte[100], 100);
            ring.RecordChunk(new byte[100], 100);
            Assert.False(ring.GetStats().TruncatedAtStart);
            Assert.Equal(2, ring.GetStats().EventCount);

            ring.RecordChunk(new byte[100], 100);

            FlightRecordingStats stats = ring.GetStats();
            Assert.Equal(2, stats.EventCount);
            Assert.True(stats.TruncatedAtStart);
            Assert.True(stats.RetainedBytes <= budget);
        }

        [Fact]
        public void RecordChunk_SingleChunkLargerThanBudget_IsRetained()
        {
            var ring = new FlightRecordingBuffer(maxTotalBytes: 64, 80, 24, clock: static () => 0);

            ring.RecordChunk(new byte[10], 10);
            ring.RecordChunk(new byte[4096], 4096); // alone exceeds the budget

            FlightRecordingStats stats = ring.GetStats();
            Assert.Equal(1, stats.EventCount); // newest survives, older evicted
            Assert.True(stats.TruncatedAtStart);
        }

        [Fact]
        public void RecordResize_FloodOverBudget_IsBoundedByEntryOverhead()
        {
            long budget = 10 * FlightRecordingBuffer.EntryOverheadBytes;
            var ring = new FlightRecordingBuffer(budget, 80, 24, clock: static () => 0);

            for (int i = 0; i < 1000; i++)
            {
                ring.RecordResize(81 + (i % 40), 25 + (i % 20));
            }

            FlightRecordingStats stats = ring.GetStats();
            Assert.True(stats.EventCount <= 10);
            Assert.True(stats.TruncatedAtStart);
        }

        [Fact]
        public void Trim_EvictingResize_AdvancesWindowStartGeometry()
        {
            // Budget fits exactly one 100-byte chunk entry; recording
            // chunk(100) → resize(132x43) → chunk(100) must evict both the first
            // chunk and the resize, leaving the window-start geometry at 132x43.
            long budget = 100 + FlightRecordingBuffer.EntryOverheadBytes;
            var ring = new FlightRecordingBuffer(budget, 80, 24, clock: static () => 0);

            ring.RecordChunk(new byte[100], 100);
            ring.RecordResize(132, 43);
            ring.RecordChunk(new byte[100], 100);

            FlightRecordingStats stats = ring.GetStats();
            Assert.Equal(132, stats.WindowStartCols);
            Assert.Equal(43, stats.WindowStartRows);
            Assert.True(stats.TruncatedAtStart);
        }

        [Fact]
        public void Trim_EvictingOnlyChunks_KeepsInitialGeometry()
        {
            long budget = 100 + FlightRecordingBuffer.EntryOverheadBytes;
            var ring = new FlightRecordingBuffer(budget, 80, 24, clock: static () => 0);

            ring.RecordChunk(new byte[100], 100);
            ring.RecordChunk(new byte[100], 100);

            FlightRecordingStats stats = ring.GetStats();
            Assert.Equal(80, stats.WindowStartCols);
            Assert.Equal(24, stats.WindowStartRows);
        }

        [Fact]
        public void Constructor_RejectsNonPositiveArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlightRecordingBuffer(0, 80, 24));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlightRecordingBuffer(1024, 0, 24));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlightRecordingBuffer(1024, 80, -1));
        }

        // ---------------------------------------------------------------
        // Export: header geometry, timestamp rebasing, truncation flag
        // ---------------------------------------------------------------

        [Fact]
        public void ExportTo_RebasesTimestampsToFirstRetainedEvent()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                var clock = new FakeClock();
                var ring = new FlightRecordingBuffer(1024 * 1024, 80, 24, clock.Read);

                clock.NowMs = 1000;
                RecordChunk(ring, "one");
                clock.NowMs = 1500;
                ring.RecordResize(100, 30);
                clock.NowMs = 2400;
                RecordChunk(ring, "two");

                FlightExportInfo info = ring.ExportTo(tempFile, "pwsh.exe");

                Assert.Equal(3, info.EventCount);
                Assert.Equal(1000, info.FirstEventMs);
                Assert.Equal(2400, info.LastEventMs);
                Assert.False(info.TruncatedAtStart);

                long[] offsets = ReadEventLines(tempFile)
                    .Select(ev => ev.TimeOffsetMs)
                    .ToArray();
                Assert.Equal(new long[] { 0, 500, 1400 }, offsets);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExportTo_HeaderCarriesWindowStartGeometry_AfterResizeEviction()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                long budget = 100 + FlightRecordingBuffer.EntryOverheadBytes;
                var ring = new FlightRecordingBuffer(budget, 80, 24, clock: static () => 0);

                ring.RecordChunk(new byte[100], 100);
                ring.RecordResize(132, 43);
                ring.RecordChunk(new byte[100], 100);

                FlightExportInfo info = ring.ExportTo(tempFile, "pwsh.exe");
                Assert.True(info.TruncatedAtStart);

                ReplayHeader header = ReadHeader(tempFile);
                Assert.Equal("novarec", header.Type);
                Assert.Equal(2, header.Version);
                Assert.Equal(132, header.Cols);
                Assert.Equal(43, header.Rows);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public async Task ExportTo_AfterResizeEviction_ReplayerAppliesPostResizeGeometryBeforeData()
        {
            // The retained window may start with a chunk that was emitted *after* a
            // resize that has since been evicted. ReplayRunner applies the header
            // geometry before replaying data, so the header must carry the
            // post-resize dimensions — otherwise that chunk would render at the old
            // width (wrong wrapping/cursor).
            string tempFile = Path.GetTempFileName();
            try
            {
                long budget = 100 + FlightRecordingBuffer.EntryOverheadBytes;
                var ring = new FlightRecordingBuffer(budget, 80, 24, clock: static () => 0);

                ring.RecordChunk(new byte[100], 100);   // 80x24 era — evicted
                ring.RecordResize(132, 43);             // evicted
                ring.RecordChunk(new byte[100], 100);   // retained; emitted at 132x43

                ring.ExportTo(tempFile, "pwsh.exe");

                var geometryCallbacks = new System.Collections.Generic.List<(int Cols, int Rows)>();
                int dataChunksBeforeFirstGeometry = 0;
                var runner = new ReplayRunner(tempFile);
                await runner.RunWithResultAsync(
                    onDataCallback: _ =>
                    {
                        if (geometryCallbacks.Count == 0) dataChunksBeforeFirstGeometry++;
                        return Task.CompletedTask;
                    },
                    onResizeCallback: (cols, rows) =>
                    {
                        geometryCallbacks.Add((cols, rows));
                        return Task.CompletedTask;
                    },
                    options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual });

                // Geometry arrives from the header before any data, and it is the
                // post-resize geometry — never the stale 80x24.
                Assert.Equal(0, dataChunksBeforeFirstGeometry);
                Assert.NotEmpty(geometryCallbacks);
                Assert.Equal((132, 43), geometryCallbacks[0]);
                Assert.DoesNotContain((80, 24), geometryCallbacks);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExportTo_EmptyRing_WritesHeaderOnlyFile()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                var ring = new FlightRecordingBuffer(1024, 80, 24, clock: static () => 0);

                FlightExportInfo info = ring.ExportTo(tempFile, "pwsh.exe");

                Assert.Equal(0, info.EventCount);
                Assert.False(info.TruncatedAtStart);

                string[] lines = File.ReadAllLines(tempFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                Assert.Single(lines);
                ReplayHeader header = ReadHeader(tempFile);
                Assert.Equal(80, header.Cols);
                Assert.Equal(24, header.Rows);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExportTo_ContainsOnlyDataAndResizeEvents()
        {
            // Privacy invariant: the ring has no input API, so an export can never
            // contain input events. Assert at the file level anyway.
            string tempFile = Path.GetTempFileName();
            try
            {
                var ring = new FlightRecordingBuffer(1024 * 1024, 80, 24, clock: static () => 0);
                RecordChunk(ring, "password prompt: ");
                ring.RecordResize(100, 30);
                RecordChunk(ring, "ok\r\n");

                ring.ExportTo(tempFile, "pwsh.exe");

                string[] types = ReadEventLines(tempFile).Select(ev => ev.Type).ToArray();
                Assert.Equal(3, types.Length);
                Assert.All(types, t => Assert.True(t == "data" || t == "resize", $"unexpected event type '{t}'"));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        // ---------------------------------------------------------------
        // Thread-safety smoke: concurrent writer + exporter
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExportTo_WhileWriterIsRecording_ProducesValidFile()
        {
            var ring = new FlightRecordingBuffer(64 * 1024, 80, 24);
            using var stop = new CancellationTokenSource();

            Task writer = Task.Run(() =>
            {
                byte[] chunk = Utf8("chunk-of-output\r\n");
                int i = 0;
                while (!stop.Token.IsCancellationRequested)
                {
                    ring.RecordChunk(chunk, chunk.Length);
                    if (++i % 50 == 0) ring.RecordResize(80 + (i % 5), 24);
                }
            });

            try
            {
                for (int round = 0; round < 5; round++)
                {
                    string tempFile = Path.GetTempFileName();
                    try
                    {
                        FlightExportInfo info = ring.ExportTo(tempFile, "pwsh.exe");

                        // Every export must be a well-formed v2 file with monotonic timestamps.
                        ReplayHeader header = ReadHeader(tempFile);
                        Assert.Equal("novarec", header.Type);

                        long previous = -1;
                        int count = 0;
                        foreach (ReplayEvent ev in ReadEventLines(tempFile))
                        {
                            Assert.True(ev.TimeOffsetMs >= previous, "timestamps must be non-decreasing");
                            previous = ev.TimeOffsetMs;
                            count++;
                        }
                        Assert.Equal(info.EventCount, count);
                    }
                    finally
                    {
                        if (File.Exists(tempFile)) File.Delete(tempFile);
                    }
                }
            }
            finally
            {
                stop.Cancel();
                await writer;
            }
        }

        // ---------------------------------------------------------------
        // PtyRecorder explicit-timestamp overloads
        // ---------------------------------------------------------------

        [Fact]
        public void PtyRecorder_ExplicitTimestampOverloads_SerializeGivenOffsets()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    byte[] data = Utf8("hello");
                    recorder.RecordChunkAt(0, data, data.Length);
                    recorder.RecordResizeAt(250, 100, 30);
                    recorder.RecordChunkAt(1300, data, data.Length);
                }

                ReplayEvent[] events = ReadEventLines(tempFile).ToArray();
                Assert.Equal(3, events.Length);
                Assert.Equal(0, events[0].TimeOffsetMs);
                Assert.Equal("data", events[0].Type);
                Assert.Equal(250, events[1].TimeOffsetMs);
                Assert.Equal("resize", events[1].Type);
                Assert.Equal(100, events[1].Cols);
                Assert.Equal(30, events[1].Rows);
                Assert.Equal(1300, events[2].TimeOffsetMs);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void PtyRecorder_ExplicitTimestampOverloads_ValidateArguments()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    Assert.Throws<ArgumentNullException>(() => recorder.RecordChunkAt(0, null!, 1));

                    byte[] data = Utf8("xyz");
                    recorder.RecordChunkAt(0, data, 0);               // empty — ignored
                    recorder.RecordChunkAt(0, data, -1);              // negative — ignored
                    recorder.RecordChunkAt(0, data, data.Length + 1); // oversized — ignored
                    recorder.RecordResizeAt(0, 0, 24);                // non-positive — ignored
                    recorder.RecordResizeAt(0, 80, -1);               // non-positive — ignored

                    recorder.RecordChunkAt(5, data, data.Length);     // valid
                }

                ReplayEvent[] events = ReadEventLines(tempFile).ToArray();
                ReplayEvent ev = Assert.Single(events);
                Assert.Equal("data", ev.Type);
                Assert.Equal(5, ev.TimeOffsetMs);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExportTo_UnwritablePath_ThrowsSynchronously()
        {
            // PtyRecorder's writer task swallows I/O failures (best-effort live
            // recording); ExportTo must surface path errors synchronously so the
            // session-level Try* surface can report false instead of "succeeding"
            // with no file.
            var ring = new FlightRecordingBuffer(1024, 80, 24, clock: static () => 0);
            RecordChunk(ring, "data");

            string missingDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nested", "export.rec");
            Assert.ThrowsAny<IOException>(() => ring.ExportTo(missingDir, "pwsh.exe"));
        }

        [Fact]
        public void PtyRecorder_ExplicitTimestampOverloads_RejectNegativeOffsets()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                using var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe");
                byte[] data = Utf8("x");
                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.RecordChunkAt(-1, data, data.Length));
                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.RecordResizeAt(-1, 80, 24));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        // ---------------------------------------------------------------
        // Round-trip acceptance criterion (DIRECTION A4): export → ReplayRunner
        // yields the same buffer as feeding the bytes directly.
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExportedFlightRecording_RoundTripsThroughReplayRunner_WithBufferParity()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // Byte script: text, a resize, colored text, and an emoji whose UTF-8
                // bytes are deliberately split across two chunks — export must
                // preserve wire-exact chunk boundaries, not re-encoded strings.
                byte[] rocket = Utf8("🚀"); // 4 bytes
                var script = new (byte[]? Chunk, (int Cols, int Rows)? Resize)[]
                {
                    (Utf8("alpha\r\nbeta"), null),
                    (null, (100, 30)),
                    (Utf8("\x1b[31mred "), null),
                    (rocket.AsSpan(0, 2).ToArray(), null),
                    (rocket.AsSpan(2, 2).ToArray(), null),
                    (Utf8(" done\x1b[0m"), null),
                };

                // 1. Feed the ring (as the PTY read loop would) and export.
                var clock = new FakeClock();
                var ring = new FlightRecordingBuffer(1024 * 1024, 80, 24, clock.Read);
                foreach (var step in script)
                {
                    clock.NowMs += 10;
                    if (step.Chunk != null) ring.RecordChunk(step.Chunk, step.Chunk.Length);
                    else ring.RecordResize(step.Resize!.Value.Cols, step.Resize.Value.Rows);
                }
                FlightExportInfo info = ring.ExportTo(tempFile, "pwsh.exe");
                Assert.Equal(script.Length, info.EventCount);

                // 2. Expected: the same bytes fed directly into a buffer.
                var expectedBuffer = new TerminalBuffer(80, 24);
                var expectedParser = new AnsiParser(expectedBuffer);
                var expectedDecoder = Encoding.UTF8.GetDecoder();
                foreach (var step in script)
                {
                    if (step.Chunk != null)
                    {
                        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(step.Chunk.Length)];
                        int n = expectedDecoder.GetChars(step.Chunk, 0, step.Chunk.Length, chars, 0);
                        if (n > 0) expectedParser.Process(new string(chars, 0, n));
                    }
                    else
                    {
                        expectedBuffer.Resize(step.Resize!.Value.Cols, step.Resize.Value.Rows);
                    }
                }

                // 3. Actual: replay the exported file through the deterministic core.
                var actualBuffer = new TerminalBuffer(80, 24);
                var actualParser = new AnsiParser(actualBuffer);
                var actualDecoder = Encoding.UTF8.GetDecoder();
                var runner = new ReplayRunner(tempFile);
                ReplayRunResult result = await runner.RunWithResultAsync(
                    onDataCallback: data =>
                    {
                        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
                        int n = actualDecoder.GetChars(data, 0, data.Length, chars, 0);
                        if (n > 0) actualParser.Process(new string(chars, 0, n));
                        return Task.CompletedTask;
                    },
                    onResizeCallback: (cols, rows) =>
                    {
                        actualBuffer.Resize(cols, rows);
                        return Task.CompletedTask;
                    },
                    options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual });

                Assert.False(result.Truncated);

                // 4. Byte-identical buffer snapshots.
                BufferSnapshot expected = BufferSnapshot.Capture(expectedBuffer, includeAttributes: true);
                BufferSnapshot actual = BufferSnapshot.Capture(actualBuffer, includeAttributes: true);
                Assert.Equal(expected.ToFormattedString(), actual.ToFormattedString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static ReplayHeader ReadHeader(string filePath)
        {
            string first = File.ReadLines(filePath).First();
            ReplayHeader? header = JsonSerializer.Deserialize(first, ReplayJsonContext.Default.ReplayHeader);
            Assert.NotNull(header);
            return header!;
        }

        private static System.Collections.Generic.IEnumerable<ReplayEvent> ReadEventLines(string filePath)
        {
            foreach (string line in File.ReadAllLines(filePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ReplayEvent? ev = JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayEvent);
                Assert.NotNull(ev);
                yield return ev!;
            }
        }
    }
}
