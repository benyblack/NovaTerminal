using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using NovaTerminal.Tests.Tools;
using System.Reflection;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests
{
    public class VttestReplayTests
    {
        private readonly string _fixturesDir;

        public VttestReplayTests()
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string runDir = Path.GetDirectoryName(assemblyPath)!;
            _fixturesDir = Path.Combine(runDir, "../../../Fixtures/Replay");
            Directory.CreateDirectory(_fixturesDir);
        }

        public static IEnumerable<object[]> VttestRecordings()
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string runDir = Path.GetDirectoryName(assemblyPath)!;
            string fixturesDir = Path.Combine(runDir, "../../../Fixtures/Replay");

            if (!Directory.Exists(fixturesDir))
                yield break;

            foreach (var rec in Directory.EnumerateFiles(fixturesDir, "vttest_*.rec"))
                yield return new object[] { Path.GetFileName(rec) };
        }

        [Theory]
        [MemberData(nameof(VttestRecordings))]
        [Trait("Category", "Replay")]
        public async Task Replay_Vttest_MatchesGoldenMaster(string recFileName)
        {
            string recPath = Path.Combine(_fixturesDir, recFileName);
            string snapPath = Path.Combine(_fixturesDir, Path.ChangeExtension(recFileName, ".snap"));

            Assert.True(File.Exists(recPath), $"Missing fixture: {recPath}");

            var buffer = new TerminalBuffer(80, 24);
            var parser = new NovaTerminal.Core.AnsiParser(buffer);

            var runner = new ReplayRunner(recPath);
            await runner.RunAsync(async (data) =>
            {
                // NOTE: current pipeline assumes UTF-8 text chunks.
                // If we later store non-UTF8 bytes, we should switch parser to ProcessBytes.
                string text = System.Text.Encoding.UTF8.GetString(data);
                parser.Process(text);
                await Task.CompletedTask;
            });

            var snapshot = BufferSnapshot.Capture(buffer);

            if (!File.Exists(snapPath) || Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
                File.WriteAllText(snapPath, snapshot.ToFormattedString());

            GoldenMaster.AssertMatches(snapshot, snapPath);
        }
    }
}
