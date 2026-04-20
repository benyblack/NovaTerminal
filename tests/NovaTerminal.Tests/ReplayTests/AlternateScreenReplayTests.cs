using System.IO;
using System.Text;
using System.Threading.Tasks;
using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests;

public sealed class AlternateScreenReplayTests
{
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_1047_ShellAppShell_RestoresMainCursorForPromptContinuation()
    {
        string recPath = Path.Combine(Path.GetTempPath(), $"nova-alt-screen-{Path.GetRandomFileName()}.rec");

        try
        {
            using (var recorder = new PtyRecorder(recPath, 20, 6))
            {
                Record(recorder, "shell\r\nprompt> ");
                Record(recorder, "\x1b[?1047h");
                Record(recorder, "\x1b[4;5HAPP");
                Record(recorder, "\x1b[?1047l");
                Record(recorder, "resume");
            }

            var buffer = new TerminalBuffer(20, 6);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async data =>
            {
                parser.Process(Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

            Assert.False(snapshot.IsAltScreen);
            Assert.Contains(snapshot.Lines, line => line == "shell");
            Assert.Contains(snapshot.Lines, line => line == "prompt> resume");
            Assert.DoesNotContain(snapshot.Lines, line => line.Contains("APP", System.StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(recPath))
            {
                File.Delete(recPath);
            }
        }
    }

    private static void Record(PtyRecorder recorder, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        recorder.RecordChunk(data, data.Length);
    }
}
