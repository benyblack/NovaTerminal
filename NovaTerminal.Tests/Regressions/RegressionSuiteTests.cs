using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using NovaTerminal.Tests.Tools;
using NovaTerminal.Tests.Infra;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Regressions
{
    public class RegressionSuiteTests
    {
        private readonly string _fixturesDir;
        private readonly ITestOutputHelper _output;

        public RegressionSuiteTests(ITestOutputHelper output)
        {
            _output = output;
            string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string runDir = Path.GetDirectoryName(assemblyPath)!;
            _fixturesDir = Path.Combine(runDir, "../../../Fixtures/Replay");
            if (!Directory.Exists(_fixturesDir)) Directory.CreateDirectory(_fixturesDir);
        }

        [Fact]
        [Trait("Category", "Regression")]
        public async Task MidnightCommander_Resize_Regression()
        {
            string recPath = Path.Combine(_fixturesDir, "mc_resize.rec");
            RecordingGenerator.GenerateMidnightCommander(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await DeadlockDetection.RunWithTimeout(async () =>
            {
                await runner.RunAsync(async (data) =>
                {
                    parser.Process(System.Text.Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                });
            }, 5000, "MC Initial Load");

            // Verify initial state: Box exists
            var snapshot = BufferSnapshot.Capture(buffer);
            Assert.Contains("Midnight Commander", snapshot.Lines[11]);

            // Simulate Resize to 100x30
            await DeadlockDetection.RunWithTimeout(async () =>
            {
                buffer.Resize(100, 30);
                // In a real scenario, the PTY would send new data. 
                // Here we just verify the buffer didn't crash or "compact" unexpectedly.
                await Task.CompletedTask;
            }, 2000, "MC Resize");

            Assert.Equal(100, buffer.Cols);
            Assert.Equal(30, buffer.Rows);
        }

        [Fact]
        [Trait("Category", "Regression")]
        public async Task OhMyPosh_SegmentOffset_Regression()
        {
            string recPath = Path.Combine(_fixturesDir, "omp_prompt.rec");
            RecordingGenerator.GenerateOhMyPosh(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async (data) =>
            {
                parser.Process(System.Text.Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            // Verify time is at col 70 (approx, depending on how GetLineText handles spaces)
            var snapshot = BufferSnapshot.Capture(buffer);
            string line0 = snapshot.Lines[0];
            Assert.Contains("10:37:00", line0);

            // Resize to 40 columns - should cause reflow
            buffer.Resize(40, 24);

            // In a bug scenario, the right segment might be lost or ghosted.
            // With proper reflow, it should still exist, likely on a new line or wrapped.
            var finalSnapshot = BufferSnapshot.Capture(buffer);
            string fullContent = string.Join("\n", finalSnapshot.Lines);

            Assert.Contains("10:37:00", fullContent);
        }
    }
}
