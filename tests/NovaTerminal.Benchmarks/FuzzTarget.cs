using System;
using System.Text;
using SharpFuzz;
using NovaTerminal.VT;

namespace NovaTerminal.Benchmarks
{
    /// <summary>
    /// SharpFuzz entry points for the VT layer. The per-input cores (<see cref="FuzzParser"/>,
    /// <see cref="FuzzParseAndResize"/>) are public and deterministic so they can be exercised
    /// directly from a unit test (see NovaTerminal.VT.Tests) without the SharpFuzz/libFuzzer
    /// instrumentation — the nightly job runs them continuously, the unit test gates each commit.
    /// </summary>
    public static class FuzzTarget
    {
        // Parser-only target. NOTE: this intentionally does NOT swallow exceptions — an unhandled
        // exception out of AnsiParser on a hostile byte stream is exactly the defect we want
        // libFuzzer to record as a crash (the previous catch-all hid every finding).
        public static void Run()
        {
            Fuzzer.LibFuzzer.Run(span => FuzzParser(span));
        }

        // Combined parse + resize target: interleaves parser input with resizes derived from the
        // same input and checks buffer invariants after every step. Exercises the reflow/resize
        // paths (#122/#123) against arbitrary parser state.
        public static void RunParseResize()
        {
            Fuzzer.LibFuzzer.Run(span => FuzzParseAndResize(span));
        }

        /// <summary>Feed an arbitrary byte stream to the parser. Throws if the parser throws.</summary>
        public static void FuzzParser(ReadOnlySpan<byte> data)
        {
            var buffer = new TerminalBuffer(80, 24);
            // A non-null decoder makes Sixel/Kitty/iTerm2 sequences exercise the image layout,
            // cursor-advance, and scroll paths instead of returning early.
            var parser = new AnsiParser(buffer) { ImageDecoder = MockImageDecoder.Instance };
            parser.Process(Encoding.UTF8.GetString(data));
        }

        /// <summary>
        /// Interleave parser input with resizes driven by the input bytes, asserting buffer
        /// invariants throughout. Throws on any invariant violation or unexpected exception.
        /// </summary>
        public static void FuzzParseAndResize(ReadOnlySpan<byte> data)
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer) { ImageDecoder = MockImageDecoder.Instance };

            int i = 0;
            while (i < data.Length)
            {
                byte op = data[i++];

                // Even op byte → feed a chunk of bytes to the parser.
                // Odd op byte  → resize to dimensions derived from the next two bytes.
                if ((op & 1) == 0)
                {
                    int len = Math.Min(1 + (op >> 1), data.Length - i);
                    if (len > 0)
                    {
                        parser.Process(Encoding.UTF8.GetString(data.Slice(i, len)));
                        i += len;
                    }
                }
                else if (i + 1 < data.Length)
                {
                    // % 220 / % 70 deliberately includes 0 so the degenerate-width guard is hit too.
                    int cols = data[i] % 220;
                    int rows = data[i + 1] % 70;
                    i += 2;
                    buffer.Resize(cols, rows);
                }

                CheckInvariants(buffer);
            }
        }

        // Invariants the buffer must uphold after any parse/resize. Thrown violations become
        // libFuzzer crashes (with the reproducing input attached) / unit-test failures.
        private static void CheckInvariants(TerminalBuffer buffer)
        {
            if (buffer.Rows <= 0 || buffer.Cols <= 0)
                throw new InvalidOperationException($"non-positive dimensions: {buffer.Cols}x{buffer.Rows}");
            if (buffer.ViewportRows.Count != buffer.Rows)
                throw new InvalidOperationException($"viewport row count {buffer.ViewportRows.Count} != Rows {buffer.Rows}");
            foreach (var row in buffer.ViewportRows)
            {
                if (row.Cells.Length != buffer.Cols)
                    throw new InvalidOperationException($"row width {row.Cells.Length} != Cols {buffer.Cols}");
            }
            if (buffer.CursorRow < 0 || buffer.CursorRow >= buffer.Rows)
                throw new InvalidOperationException($"cursor row {buffer.CursorRow} out of [0,{buffer.Rows})");
            if (buffer.CursorCol < 0 || buffer.CursorCol > buffer.Cols)
                throw new InvalidOperationException($"cursor col {buffer.CursorCol} out of [0,{buffer.Cols}]");
        }

        // Returns dummy dimensions + a non-null handle so image control sequences drive the
        // layout/cursor/scroll paths during fuzzing instead of bailing out at a null decoder.
        private sealed class MockImageDecoder : IImageDecoder
        {
            public static readonly MockImageDecoder Instance = new();

            public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
            {
                pixelWidth = 100;
                pixelHeight = 100;
                return Instance;
            }

            public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
            {
                pixelWidth = 100;
                pixelHeight = 100;
                return Instance;
            }
        }
    }
}
