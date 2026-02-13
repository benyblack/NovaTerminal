using BenchmarkDotNet.Running;
using System;

namespace NovaTerminal.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--fuzz")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: --fuzz <input_file>");
                    return;
                }
                FuzzTarget.Run(args[1]);
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
