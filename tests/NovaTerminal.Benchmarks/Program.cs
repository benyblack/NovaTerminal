using BenchmarkDotNet.Running;
using System;

namespace NovaTerminal.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            // SharpFuzz/libFuzzer entry points. The input is supplied by the libFuzzer driver
            // (via SharpFuzz), so no input-file argument is needed here.
            if (args.Length > 0 && args[0] == "--fuzz")
            {
                FuzzTarget.Run();
                return;
            }

            if (args.Length > 0 && args[0] == "--fuzz-resize")
            {
                FuzzTarget.RunParseResize();
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
