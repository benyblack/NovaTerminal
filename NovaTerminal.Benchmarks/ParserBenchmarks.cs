using BenchmarkDotNet.Attributes;
using NovaTerminal.Core;
using System.Text;

namespace NovaTerminal.Benchmarks
{
    [MemoryDiagnoser]
    public class ParserBenchmarks
    {
        private TerminalBuffer? _buffer;
        private AnsiParser? _parser;
        private string? _normalText;
        private string? _ansiHeavy;
        private string? _wideChars;

        [GlobalSetup]
        public void Setup()
        {
            _buffer = new TerminalBuffer(80, 24);
            _parser = new AnsiParser(_buffer);

            // 1MB of normal text
            _normalText = new string('A', 1024 * 1024);

            // 1MB of ANSI-heavy text (alternating colors every few chars)
            var sbAnsi = new StringBuilder();
            for (int i = 0; i < 50000; i++)
            {
                sbAnsi.Append("\x1b[31mR\x1b[32mG\x1b[34mB\x1b[0m ");
                sbAnsi.Append("Text ");
                if (i % 10 == 0) sbAnsi.Append("\r\n");
            }
            _ansiHeavy = sbAnsi.ToString();

            // 1MB of Wide chars (Emojis and CJK)
            var sbWide = new StringBuilder();
            for (int i = 0; i < 100000; i++)
            {
                sbWide.Append("😊你好🌟");
                if (i % 20 == 0) sbWide.Append("\r\n");
            }
            _wideChars = sbWide.ToString();
        }

        [Benchmark]
        public void Throughput_NormalText()
        {
            _parser!.Process(_normalText!);
        }

        [Benchmark]
        public void Throughput_AnsiHeavy()
        {
            _parser!.Process(_ansiHeavy!);
        }

        [Benchmark]
        public void Throughput_WideChars()
        {
            _parser!.Process(_wideChars!);
        }
    }
}
