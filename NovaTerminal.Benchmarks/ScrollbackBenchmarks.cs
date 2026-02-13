using BenchmarkDotNet.Attributes;
using NovaTerminal.Core;

namespace NovaTerminal.Benchmarks
{
    [MemoryDiagnoser]
    public class ScrollbackBenchmarks
    {
        private TerminalBuffer? _buffer;
        private AnsiParser? _parser;
        private string? _batch;

        [Params(1000, 10000, 100000)]
        public int ScrollbackSize;

        [GlobalSetup]
        public void Setup()
        {
            _buffer = new TerminalBuffer(80, ScrollbackSize);
            _parser = new AnsiParser(_buffer);

            // Fill it up first
            for (int i = 0; i < ScrollbackSize; i++)
            {
                _buffer.Write("Padding Line\r\n");
            }

            _batch = new string('A', 80 * 100) + "\r\n"; // 100 lines of text
        }

        [Benchmark]
        public void EvictionPerformance()
        {
            _parser!.Process(_batch!);
        }
    }
}
