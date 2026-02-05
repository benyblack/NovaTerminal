using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System.Text;

namespace NovaTerminal.Tests.ReplayTests
{
    public class ReplayInfraTests
    {
        [Fact]
        public async Task Recorder_RoundTrip_Works()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // 1. Record
                using (var recorder = new PtyRecorder(tempFile))
                {
                    byte[] data1 = Encoding.UTF8.GetBytes("Hello ");
                    byte[] data2 = Encoding.UTF8.GetBytes("World");

                    recorder.RecordChunk(data1, data1.Length);
                    await Task.Delay(10); // Ensure timestamp diff
                    recorder.RecordChunk(data2, data2.Length);
                }

                // 2. Replay
                var gathered = new StringBuilder();
                var runner = new ReplayRunner(tempFile);

                await runner.RunAsync(async (data) =>
                {
                    gathered.Append(Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                });

                // 3. Assert
                Assert.Equal("Hello World", gathered.ToString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void Snapshot_Captures_Viewport()
        {
            // Setup a buffer
            var buffer = new TerminalBuffer(20, 5);
            buffer.Write("Line 1");
            buffer.Write("\r\nLine 2");

            // Capture
            var snapshot = BufferSnapshot.Capture(buffer);

            // Assert
            Assert.Equal(6, snapshot.CursorCol); // "Line 2" is 6 chars
            Assert.Equal(1, snapshot.CursorRow); // Line 2 is index 1
            Assert.Equal(5, snapshot.Lines.Length);
            Assert.StartsWith("Line 1", snapshot.Lines[0]);
            Assert.StartsWith("Line 2", snapshot.Lines[1]);
        }
    }
}
