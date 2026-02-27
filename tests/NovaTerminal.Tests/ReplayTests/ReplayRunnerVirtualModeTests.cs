using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests
{
    public class ReplayRunnerVirtualModeTests
    {
        [Fact]
        public async Task ReplayRunner_VirtualMode_ProcessesEventsAndMatchesSnapshot()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                const int cols = 100;
                const int rows = 30;
                const string payload = "alpha\r\nbeta\r\ngamma";

                var expectedBuffer = new TerminalBuffer(80, 24);
                var expectedParser = new AnsiParser(expectedBuffer);
                expectedBuffer.Resize(cols, rows);
                expectedParser.Process(payload);

                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    recorder.RecordResize(cols, rows);
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    recorder.RecordChunk(bytes, bytes.Length);
                    recorder.RecordSnapshot(expectedBuffer);
                }

                var actualBuffer = new TerminalBuffer(80, 24);
                var actualParser = new AnsiParser(actualBuffer);
                var runner = new ReplayRunner(tempFile);
                ReplayRunResult result = await runner.RunWithResultAsync(
                    onDataCallback: data =>
                    {
                        actualParser.Process(Encoding.UTF8.GetString(data));
                        return Task.CompletedTask;
                    },
                    onResizeCallback: (newCols, newRows) =>
                    {
                        actualBuffer.Resize(newCols, newRows);
                        return Task.CompletedTask;
                    },
                    onSnapshotCallback: snapshot =>
                    {
                        actualBuffer.ApplySnapshot(snapshot);
                        return Task.CompletedTask;
                    },
                    options: new ReplayRunOptions
                    {
                        PlaybackMode = ReplayPlaybackMode.Virtual
                    });

                Assert.False(result.Truncated);
                Assert.Equal(3, result.EventsProcessed);

                BufferSnapshot expected = BufferSnapshot.Capture(expectedBuffer, includeAttributes: true);
                BufferSnapshot actual = BufferSnapshot.Capture(actualBuffer, includeAttributes: true);
                Assert.Equal(expected.ToFormattedString(), actual.ToFormattedString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
