using BenchmarkDotNet.Attributes;
using NovaTerminal.Core;

namespace NovaTerminal.Benchmarks
{
    [MemoryDiagnoser]
    public class ReflowBenchmarks
    {
        private TerminalBuffer? _smallBuffer;
        private TerminalBuffer? _largeBuffer;
        private int _toggle = 0;

        [GlobalSetup]
        public void Setup()
        {
            _smallBuffer = new TerminalBuffer(80, 24);
            for (int i = 0; i < 50; i++) _smallBuffer.Write($"Line {i}\r\n");

            _largeBuffer = new TerminalBuffer(80, 10000); // 10k lines
            for (int i = 0; i < 10000; i++)
            {
                _largeBuffer.Write($"Line {i:D4} Content Content Content Content Content\r\n");
            }
        }

        [Benchmark]
        public void Reflow_SmallBuffer()
        {
            _toggle = (_toggle == 0) ? 1 : 0;
            _smallBuffer!.Resize(70 + _toggle, 24);
        }

        [Benchmark]
        public void Reflow_LargeBuffer()
        {
            _toggle = (_toggle == 0) ? 1 : 0;
            _largeBuffer!.Resize(70 + _toggle, 24);
        }
    }
}
