using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NovaTerminal.ExternalSuites.NativeSsh;
using NovaTerminal.ExternalSuites.Vttest;

namespace NovaTerminal.ExternalSuites
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            string suite = "vttest";
            string scenario = "cursor";
            string outputPath = "output.rec";
            ushort cols = 80;
            ushort rows = 24;
            int timeoutMs = 30000;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--suite": suite = args[++i]; break;
                    case "--scenario": scenario = args[++i]; break;
                    case "--out": outputPath = args[++i]; break;
                    case "--cols": cols = ushort.Parse(args[++i]); break;
                    case "--rows": rows = ushort.Parse(args[++i]); break;
                    case "--timeout": timeoutMs = int.Parse(args[++i]); break;
                }
            }

            if (suite != "vttest")
            {
                if (suite != "native-ssh")
                {
                    Console.WriteLine($"Unknown suite: {suite}");
                    return 1;
                }

                Console.WriteLine($"[NativeSshAdapter] Scenario: {scenario}, Size: {cols}x{rows}, Out: {outputPath}");
                await using var nativeWriter = new RecWriter(outputPath);

                try
                {
                    var driver = new NativeSshTranscriptDriver(nativeWriter);
                    var executionTask = driver.ExecuteAsync(NativeSshScenarioPlan.GetScenario(scenario), cols, rows);
                    var timeoutTask = Task.Delay(timeoutMs);
                    var completedTask = await Task.WhenAny(executionTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        Console.WriteLine("[NativeSshAdapter] Error: Scenario timed out.");
                        return 1;
                    }

                    await executionTask;
                    Console.WriteLine("[NativeSshAdapter] Success: .rec file generated.");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NativeSshAdapter] Failed: {ex.Message}");
                    return 1;
                }
            }

            Console.WriteLine($"[VttestAdapter] Scenario: {scenario}, Size: {cols}x{rows}, Out: {outputPath}");

            IEnumerable<Step> steps = scenario switch
            {
                "cursor" => VttestPlan.GetCursorScenario(),
                "sgr" => VttestPlan.GetSgrScenario(),
                "scroll" => VttestPlan.GetScrollScenario(),
                _ => throw new ArgumentException($"Unknown scenario: {scenario}")
            };

            // Ensure output directory exists
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var recWriter = new RecWriter(outputPath);

            // On Linux, vttest is usually just 'vttest'. On Windows, we might need to specify a path or use a mock.
            // For this implementation, we assume 'vttest' is in PATH.
            string vttestCmd = "vttest";
            string vttestArgs = "";

            try
            {
                await using var capture = new VttestCapture(vttestCmd, vttestArgs, cols, rows, recWriter);
                var driver = new VttestDriver(capture);

                var executionTask = driver.ExecuteStepsAsync(steps);
                var timeoutTask = Task.Delay(timeoutMs);

                var completedTask = await Task.WhenAny(executionTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Console.WriteLine("[VttestAdapter] Error: Scenario timed out.");
                    return 1;
                }

                await executionTask;
                Console.WriteLine("[VttestAdapter] Success: .rec file generated.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VttestAdapter] Failed: {ex.Message}");
                return 1;
            }
        }
    }
}
