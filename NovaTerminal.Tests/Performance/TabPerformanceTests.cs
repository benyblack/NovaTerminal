using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using NovaTerminal.Core;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Performance
{
    public class TabPerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public TabPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void TabMruSwitch_P95_UnderBudget()
        {
            const int tabCount = 10;
            const int samplesCount = 10_000;
            var samplesMs = new double[samplesCount];

            int currentIndex = 0;
            for (int i = 0; i < samplesCount; i++)
            {
                bool reverse = (i & 1) == 1;
                var sw = Stopwatch.StartNew();
                currentIndex = NovaTerminal.MainWindow.GetNextMruIndex(currentIndex, tabCount, reverse);
                sw.Stop();
                samplesMs[i] = sw.Elapsed.TotalMilliseconds;
            }

            Assert.InRange(currentIndex, 0, tabCount - 1);
            double p95 = Percentile(samplesMs, 95);
            _output.WriteLine($"MRU switch p95: {p95:F4} ms");

            // Plan budget: p95 < 35ms with 10 tabs.
            Assert.True(p95 < 35, $"MRU switch p95 exceeded budget: {p95:F4} ms >= 35 ms");
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void TabOverflowComputation_P95_UnderBudget()
        {
            const int tabCount = 20;
            const int samplesCount = 5_000;
            const double viewportWidth = 600;
            var widths = Enumerable.Repeat(120d, tabCount).ToArray();
            var samplesMs = new double[samplesCount];

            int hiddenTotal = 0;
            for (int i = 0; i < samplesCount; i++)
            {
                // Simulate dynamic header widths from title/activity changes.
                widths[i % tabCount] = 90 + (i % 7) * 18;

                var sw = Stopwatch.StartNew();
                hiddenTotal += NovaTerminal.MainWindow.CountHiddenTabs(viewportWidth, widths);
                sw.Stop();
                samplesMs[i] = sw.Elapsed.TotalMilliseconds;
            }

            Assert.True(hiddenTotal > 0);
            double p95 = Percentile(samplesMs, 95);
            _output.WriteLine($"Overflow compute p95: {p95:F4} ms");

            // Guardrail: this should remain far below interactive frame-time budgets.
            Assert.True(p95 < 5, $"Overflow computation p95 exceeded budget: {p95:F4} ms >= 5 ms");
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void SessionRoundTrip_20Tabs_P95_UnderBudget()
        {
            const int samplesCount = 250;
            var samplesMs = new double[samplesCount];
            var session = BuildSession(tabCount: 20);

            for (int i = 0; i < samplesCount; i++)
            {
                var sw = Stopwatch.StartNew();
                string json = JsonSerializer.Serialize(session, SessionSerializationContext.Default.NovaSession);
                var restored = JsonSerializer.Deserialize(json, SessionSerializationContext.Default.NovaSession);
                sw.Stop();

                Assert.NotNull(restored);
                Assert.Equal(20, restored!.Tabs.Count);
                samplesMs[i] = sw.Elapsed.TotalMilliseconds;
            }

            double p95 = Percentile(samplesMs, 95);
            _output.WriteLine($"Session round-trip(20 tabs) p95: {p95:F4} ms");

            // Plan guardrail for tab lifecycle/session operations.
            Assert.True(p95 < 120, $"Session round-trip p95 exceeded budget: {p95:F4} ms >= 120 ms");
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
                    UserTitle = $"Work {i + 1}",
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

        private static double Percentile(IReadOnlyList<double> values, int percentile)
        {
            if (values.Count == 0) return 0;
            if (percentile <= 0) return values.Min();
            if (percentile >= 100) return values.Max();

            var sorted = values.OrderBy(v => v).ToArray();
            int index = (int)Math.Ceiling((percentile / 100.0) * sorted.Length) - 1;
            index = Math.Clamp(index, 0, sorted.Length - 1);
            return sorted[index];
        }
    }
}
