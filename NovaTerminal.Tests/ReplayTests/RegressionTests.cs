using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using NovaTerminal.Tests.Tools;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests
{
    public class RegressionTests
    {
        private readonly string _fixturesDir;

        public RegressionTests()
        {
            // Calculate absolute path to Fixtures/Replay relative to execution directory
            // Typically bin/Debug/net10.0/../../../Fixtures/Replay
            // This relies on the project structure.
            // Better: use a known relative path from the test assembly location.
            string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string runDir = Path.GetDirectoryName(assemblyPath)!;
            // Up 3 levels to project root, then Fixtures
            _fixturesDir = Path.Combine(runDir, "../../../Fixtures/Replay");

            if (!Directory.Exists(_fixturesDir)) Directory.CreateDirectory(_fixturesDir);
        }

        [Fact]
        public async Task Replay_HelloWorld_MatchesGoldenMaster()
        {
            string recPath = Path.Combine(_fixturesDir, "hello_world.rec");
            string snapPath = Path.Combine(_fixturesDir, "hello_world.snap");

            // 0. Ensure Fixtures Exist (Self-bootstrapping for dev convenience)
            if (!File.Exists(recPath))
            {
                RecordingGenerator.GenerateHelloWorld(recPath);
            }

            // 1. Setup Headless Environment
            var buffer = new TerminalBuffer(80, 24);
            var parser = new NovaTerminal.Core.AnsiParser(buffer);

            // 2. Run Replay
            var runner = new ReplayRunner(recPath);
            await runner.RunAsync(async (data) =>
            {
                string text = System.Text.Encoding.UTF8.GetString(data);
                parser.Process(text);
                await Task.CompletedTask;
            });

            // 3. Capture Snapshot
            var snapshot = BufferSnapshot.Capture(buffer);

            // 4. Update Golden Master if requested (Environment Variable)
            // or if it doesn't exist yet (Bootstrap)
            if (!File.Exists(snapPath) || System.Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
            {
                File.WriteAllText(snapPath, snapshot.ToFormattedString());
            }

            // 5. Assert
            GoldenMaster.AssertMatches(snapshot, snapPath);
        }
    }
}
