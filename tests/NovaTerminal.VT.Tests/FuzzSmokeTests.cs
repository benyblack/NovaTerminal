using System;
using System.Text;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// Deterministic, seeded counterpart to the SharpFuzz harness (#124). The nightly fuzzer explores
// inputs continuously; these tests pin a handful of seeds so the same parse + resize robustness
// guarantee (never throw, buffer invariants always hold) is checked on every commit. They mirror
// the logic in NovaTerminal.Benchmarks/FuzzTarget.cs but stay self-contained (no SharpFuzz dep).
public class FuzzSmokeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(99991)]
    public void RandomBytes_DoNotThrow_ParserStaysUsable(int seed)
    {
        var rng = new Random(seed);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var data = new byte[rng.Next(0, 512)];
            rng.NextBytes(data);
            // Must not throw on any byte stream.
            ParseAndResize(data);
        }
    }

    // Same shape as FuzzTarget.FuzzParseAndResize: interleave parser input and resizes driven by
    // the input bytes, asserting invariants after every step.
    private static void ParseAndResize(ReadOnlySpan<byte> data)
    {
        var buffer = new TerminalBuffer(80, 24);
        // Mock decoder so image sequences exercise layout/cursor/scroll paths, not an early return.
        var parser = new AnsiParser(buffer) { ImageDecoder = MockImageDecoder.Instance };

        int i = 0;
        while (i < data.Length)
        {
            byte op = data[i++];
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
                int cols = data[i] % 220; // includes 0 → exercises the degenerate-width guard
                int rows = data[i + 1] % 70;
                i += 2;
                buffer.Resize(cols, rows);
            }

            CheckInvariants(buffer);
        }
    }

    private static void CheckInvariants(TerminalBuffer buffer)
    {
        Assert.True(buffer.Rows > 0 && buffer.Cols > 0, $"non-positive dimensions: {buffer.Cols}x{buffer.Rows}");
        Assert.Equal(buffer.Rows, buffer.ViewportRows.Count);
        foreach (var row in buffer.ViewportRows)
        {
            Assert.Equal(buffer.Cols, row.Cells.Length);
        }
        Assert.InRange(buffer.CursorRow, 0, buffer.Rows - 1);
        Assert.InRange(buffer.CursorCol, 0, buffer.Cols);
    }

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
