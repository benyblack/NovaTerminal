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

        [Fact]
        public async Task Replay_VimExit_RestoresMainBuffer()
        {
            string recPath = Path.Combine(_fixturesDir, "vim_exit.rec");
            string snapPath = Path.Combine(_fixturesDir, "vim_exit.snap");

            // Regenerate fixture to ensure it matches current logic intent
            RecordingGenerator.GenerateVimExit(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new NovaTerminal.Core.AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async (data) =>
            {
                string text = System.Text.Encoding.UTF8.GetString(data);
                parser.Process(text);
                await Task.CompletedTask;
            });

            var snapshot = BufferSnapshot.Capture(buffer);

            // Check specific expectation: "Before Vim" should be there, "Inside Vim" should NOT
            bool hasBefore = false;
            bool hasInside = false;
            foreach (var line in snapshot.Lines)
            {
                if (line.Contains("Before Vim")) hasBefore = true;
                if (line.Contains("Inside Vim")) hasInside = true;
            }

            Assert.True(hasBefore, "Main sceren content 'Before Vim' was lost.");
            Assert.False(hasInside, "Alt screen content 'Inside Vim' leaked into Main screen.");

            // Also check standard Golden Master
            if (!File.Exists(snapPath) || System.Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
            {
                File.WriteAllText(snapPath, snapshot.ToFormattedString());
            }
            GoldenMaster.AssertMatches(snapshot, snapPath);
        }

        [Fact]
        public async Task Replay_AltScreenCursor_ScopesIdeally()
        {
            string recPath = Path.Combine(_fixturesDir, "alt_screen_cursor.rec");
            string snapPath = Path.Combine(_fixturesDir, "alt_screen_cursor.snap");

            RecordingGenerator.GenerateAltScreenCursor(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new NovaTerminal.Core.AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async (data) =>
            {
                string text = System.Text.Encoding.UTF8.GetString(data);
                parser.Process(text);
                await Task.CompletedTask;
            });

            var snapshot = BufferSnapshot.Capture(buffer);

            // Expectation: Cursor should be at (10, 10)
            // If it is at (5, 5), then the Alt Screen cursor overwrite the Main Screen saved cursor.

            // To be robust, saving Snapshot if needed
            if (!File.Exists(snapPath) || System.Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
            {
                File.WriteAllText(snapPath, snapshot.ToFormattedString());
            }

            Assert.Equal(10, buffer.CursorCol);
            Assert.Equal(10, buffer.CursorRow);
        }
    }
}
