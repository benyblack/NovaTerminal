using System;
using SharpFuzz;
using NovaTerminal.Core;

namespace NovaTerminal.Benchmarks
{
    public static class FuzzTarget
    {
        public static void Run(string inputPath)
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Using OutOfProcess for AFL/libFuzzer compatibility if needed
            Fuzzer.LibFuzzer.Run(span =>
            {
                try
                {
                    string data = System.Text.Encoding.UTF8.GetString(span);
                    parser.Process(data);
                }
                catch (Exception)
                {
                }
            });
        }
    }
}
