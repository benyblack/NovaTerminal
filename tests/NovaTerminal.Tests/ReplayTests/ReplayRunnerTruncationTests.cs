using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests
{
    public class ReplayRunnerTruncationTests
    {
        [Fact]
        public async Task ReplayRunner_VirtualMode_TruncatedTail_ReturnsTruncatedResult()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                string header = "{\"type\":\"novarec\",\"v\":2,\"cols\":40,\"rows\":5,\"date\":\"2026-01-01T00:00:00.0000000Z\",\"shell\":\"pwsh.exe\"}";
                string event1 = BuildDataEvent(10, "Hello");
                string event2 = BuildDataEvent(20, " World");
                string event3 = BuildDataEvent(30, " !!!");

                string truncatedEvent3 = event3.Substring(0, event3.Length / 2);
                File.WriteAllText(tempFile, $"{header}\n{event1}\n{event2}\n{truncatedEvent3}");

                var expectedBuffer = new TerminalBuffer(40, 5);
                var expectedParser = new AnsiParser(expectedBuffer);
                expectedParser.Process("Hello World");
                BufferSnapshot expected = BufferSnapshot.Capture(expectedBuffer, includeAttributes: true);

                var actualBuffer = new TerminalBuffer(40, 5);
                var actualParser = new AnsiParser(actualBuffer);
                var runner = new ReplayRunner(tempFile);

                ReplayRunResult result = await runner.RunWithResultAsync(
                    onDataCallback: data =>
                    {
                        actualParser.Process(Encoding.UTF8.GetString(data));
                        return Task.CompletedTask;
                    },
                    options: new ReplayRunOptions
                    {
                        PlaybackMode = ReplayPlaybackMode.Virtual
                    });

                Assert.True(result.Truncated);
                Assert.Equal(2, result.EventsProcessed);
                Assert.Equal(20, result.LastGoodOffset);

                BufferSnapshot actual = BufferSnapshot.Capture(actualBuffer, includeAttributes: true);
                Assert.Equal(expected.ToFormattedString(), actual.ToFormattedString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        private static string BuildDataEvent(long timeMs, string payload)
        {
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            return $"{{\"t\":{timeMs},\"type\":\"data\",\"d\":\"{base64}\"}}";
        }
    }
}
