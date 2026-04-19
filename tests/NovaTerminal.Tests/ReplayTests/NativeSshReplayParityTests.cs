using System.Reflection;
using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using NovaTerminal.Tests.Tools;

namespace NovaTerminal.Tests.ReplayTests;

public sealed class NativeSshReplayParityTests
{
    private readonly string _fixturesDir;

    public NativeSshReplayParityTests()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string runDir = Path.GetDirectoryName(assemblyPath)!;
        _fixturesDir = Path.Combine(runDir, "../../../Fixtures/Replay");
        Directory.CreateDirectory(_fixturesDir);
    }

    [Theory]
    [InlineData("native_ssh_fullscreen_exit.rec")]
    [InlineData("native_ssh_prompt_return.rec")]
    [InlineData("native_ssh_resize_burst.rec")]
    [Trait("Category", "Replay")]
    public async Task Replay_NativeSsh_MatchesGoldenMaster(string recFileName)
    {
        string recPath = Path.Combine(_fixturesDir, recFileName);
        string snapPath = Path.Combine(_fixturesDir, Path.ChangeExtension(recFileName, ".snap"));

        Assert.True(File.Exists(recPath), $"Missing fixture: {recPath}");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var runner = new ReplayRunner(recPath);

        await runner.RunAsync(
            async data =>
            {
                parser.Process(System.Text.Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            },
            async (cols, rows) =>
            {
                buffer.Resize(cols, rows);
                await Task.CompletedTask;
            });

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);
        AssertScenarioShape(recFileName, snapshot);

        if (!File.Exists(snapPath) || FixtureUpdatePolicy.ShouldUpdateSnapshots())
        {
            File.WriteAllText(snapPath, snapshot.ToFormattedString());
        }

        GoldenMaster.AssertMatches(snapshot, snapPath);
    }

    private static void AssertScenarioShape(string recFileName, BufferSnapshot snapshot)
    {
        Assert.False(snapshot.IsAltScreen);

        switch (recFileName)
        {
            case "native_ssh_fullscreen_exit.rec":
                Assert.Contains(snapshot.Lines, line => line.Contains("Connected to native.example", StringComparison.Ordinal));
                Assert.Contains(snapshot.Lines, line => line.Contains("nova$ mc", StringComparison.Ordinal));
                Assert.Contains(snapshot.Lines, line => line.Contains("nova$", StringComparison.Ordinal));
                Assert.DoesNotContain(snapshot.Lines, line => line.Contains("Midnight Commander", StringComparison.Ordinal));
                break;
            case "native_ssh_prompt_return.rec":
                Assert.Contains(snapshot.Lines, line => line.Contains("nova$ echo hi", StringComparison.Ordinal));
                Assert.Contains(snapshot.Lines, line => line == "hi");
                Assert.Contains(snapshot.Lines, line => line.Contains("done", StringComparison.Ordinal));
                Assert.Contains(snapshot.Lines, line => line == "nova$");
                break;
            case "native_ssh_resize_burst.rec":
                Assert.Contains(snapshot.Lines, line => line.Contains("nova$ mc", StringComparison.Ordinal));
                Assert.Contains(snapshot.Lines, line => line == "nova$");
                Assert.DoesNotContain(snapshot.Lines, line => line.Contains("TUI 90x20", StringComparison.Ordinal));
                break;
        }
    }
}
