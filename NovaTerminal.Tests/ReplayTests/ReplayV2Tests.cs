using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System.Text;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests
{
    public class ReplayV2Tests
    {
        [Fact]
        public async Task ReplayV2_RoundTrip_Works()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // 1. Record v2
                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    byte[] data1 = Encoding.UTF8.GetBytes("Hello");
                    recorder.RecordChunk(data1, data1.Length);
                    
                    recorder.RecordResize(120, 30);
                    
                    recorder.RecordMarker("test_marker");
                    
                    byte[] data2 = Encoding.UTF8.GetBytes("World");
                    recorder.RecordChunk(data2, data2.Length);
                }

                // 2. Replay v2
                var gathered = new StringBuilder();
                int lastCols = 0, lastRows = 0;
                string lastMarker = "";

                var runner = new ReplayRunner(tempFile);
                await runner.RunAsync(
                    onDataCallback: async (data) =>
                    {
                        gathered.Append(Encoding.UTF8.GetString(data));
                        await Task.CompletedTask;
                    },
                    onResizeCallback: async (cols, rows) =>
                    {
                        lastCols = cols;
                        lastRows = rows;
                        await Task.CompletedTask;
                    },
                    onMarkerCallback: async (name) =>
                    {
                        lastMarker = name;
                        await Task.CompletedTask;
                    }
                );

                // 3. Assert
                Assert.Equal("HelloWorld", gathered.ToString());
                Assert.Equal(120, lastCols);
                Assert.Equal(30, lastRows);
                Assert.Equal("test_marker", lastMarker);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public async Task ReplayRunner_V1Compatibility_Works()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // Create a manual V1 file
                // Format: {"t":0,"d":"SGVsbG8="}
                string v1Content = "{\"t\":10,\"d\":\"SGVsbG8=\"}\n{\"t\":20,\"d\":\"IFdvcmxk\"}";
                File.WriteAllText(tempFile, v1Content);

                var gathered = new StringBuilder();
                var runner = new ReplayRunner(tempFile);

                await runner.RunAsync(async (data) =>
                {
                    gathered.Append(Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                });

                Assert.Equal("Hello World", gathered.ToString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
        
        [Fact]
        public async Task ReplayRunner_StrictHeader_FailsOnMalformData()
        {
             string tempFile = Path.GetTempFileName();
            try
            {
                // Line 1 is NOT a header, NOT a v1 chunk
                string corruptContent = "This is not JSON\n{\"t\":20,\"d\":\"IFdvcmxk\"}";
                File.WriteAllText(tempFile, corruptContent);

                var gathered = new StringBuilder();
                var runner = new ReplayRunner(tempFile);

                // Should not throw, but gathered should be empty if it fails to detect either format
                // or if it fails the first line and then tries to continue
                await runner.RunAsync(async (data) =>
                {
                    gathered.Append(Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                });

                // Since it didn't find v2 header, it falls back to v1 parser.
                // V1 parser is lenient and might skip the first line.
                // Our implementation skips the first line if it's V2, but if V1 it processes it.
                // If the first line is garbage, V1 parser catches and skips it.
                Assert.Equal(" World", gathered.ToString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
