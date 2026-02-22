using NovaTerminal.Core;
using System;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    public class MemoryLeakTest
    {
        private readonly ITestOutputHelper _output;

        public MemoryLeakTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Stress")]
        public void Resize_MemoryStability_StressTest()
        {
            var buffer = new TerminalBuffer(80, 24);
            long initialMemory = GC.GetTotalMemory(true);

            _output.WriteLine($"Initial memory: {initialMemory / 1024.0 / 1024.0:F2} MB");

            for (int i = 0; i < 1000; i++)
            {
                // Rapidly resize and write
                buffer.Resize(40 + (i % 80), 20 + (i % 40));
                buffer.Write($"Stress Line {i}\r\n");

                if (i % 100 == 0)
                {
                    _output.WriteLine($"Iteration {i}...");
                }
            }

            // Cleanup
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            _output.WriteLine($"Final memory after 1000 resizes: {finalMemory / 1024.0 / 1024.0:F2} MB");

            // Threshold: Memory should not grow unboundedly. 
            // 50MB of overhead is reasonable for fragmentation, but 500MB would be a leak.
            long diff = finalMemory - initialMemory;
            _output.WriteLine($"Growth: {diff / 1024.0 / 1024.0:F2} MB");

            Assert.True(diff < 50 * 1024 * 1024, $"Memory leak detected! Growth: {diff / 1024.0 / 1024.0:F2} MB");
        }

        [Fact]
        [Trait("Category", "Stress")]
        public void TabLifecycle_CreateCloseLoop_5000Iterations_MemoryStability()
        {
            const int iterations = 5_000;
            long initialMemory = GC.GetTotalMemory(true);
            _output.WriteLine($"Initial memory: {initialMemory / 1024.0 / 1024.0:F2} MB");

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var session = BuildSession(1 + (i % 20));
                string json = JsonSerializer.Serialize(session, SessionSerializationContext.Default.NovaSession);
                var restored = JsonSerializer.Deserialize(json, SessionSerializationContext.Default.NovaSession);

                Assert.NotNull(restored);
                restored!.Tabs.Clear(); // Simulate close-all lifecycle.

                if ((i + 1) % 1000 == 0)
                {
                    _output.WriteLine($"Iteration {i + 1}/{iterations}");
                }
            }
            sw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            long diff = finalMemory - initialMemory;

            _output.WriteLine($"Loop duration: {sw.Elapsed.TotalSeconds:F2}s");
            _output.WriteLine($"Final memory: {finalMemory / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"Growth: {diff / 1024.0 / 1024.0:F2} MB");

            // Guardrail for lifecycle loop pressure in CI and local runs.
            Assert.True(diff < 120 * 1024 * 1024, $"Potential tab lifecycle leak detected! Growth: {diff / 1024.0 / 1024.0:F2} MB");
            Assert.True(sw.Elapsed.TotalSeconds < 30, $"Lifecycle loop too slow: {sw.Elapsed.TotalSeconds:F2}s");
        }

        private static NovaSession BuildSession(int tabCount)
        {
            var session = new NovaSession { ActiveTabIndex = 0 };

            for (int i = 0; i < tabCount; i++)
            {
                string paneId = $"pane-{i}";
                session.Tabs.Add(new TabSession
                {
                    TabId = Guid.NewGuid().ToString("D"),
                    Title = $"Terminal {i + 1}",
                    UserTitle = $"Loop {i + 1}",
                    IsPinned = (i % 5) == 0,
                    IsProtected = (i % 7) == 0,
                    ActivePaneId = paneId,
                    ZoomedPaneId = (i % 3) == 0 ? paneId : null,
                    BroadcastInputEnabled = (i % 4) == 0,
                    Root = new PaneNode
                    {
                        Type = NodeType.Leaf,
                        PaneId = paneId,
                        Command = "bash",
                        Arguments = "-l"
                    }
                });
            }

            return session;
        }
    }
}
