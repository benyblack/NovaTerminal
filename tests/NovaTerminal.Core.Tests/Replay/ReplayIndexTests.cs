using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using NovaTerminal.Core.Replay;
using System.Collections.Generic;

namespace NovaTerminal.Core.Tests.Replay
{
    public class ReplayIndexTests : IDisposable
    {
        private readonly string _testFile;

        public ReplayIndexTests()
        {
            _testFile = Path.GetTempFileName();
            var lines = new List<string>
            {
                // V2 Header
                "{\"type\":\"novarec\",\"v\":2,\"cols\":80,\"rows\":24,\"date\":\"2026-03-04T00:00:00.0000000Z\",\"shell\":\"bash\"}",
                // Events
                "{\"t\":100,\"type\":\"data\",\"d\":\"SGVsbG8=\"}",
                "{\"t\":200,\"type\":\"data\",\"d\":\"IFdvcmxk\"}",
                "{\"t\":350,\"type\":\"data\",\"d\":\"IQ==\"}",
            };
            File.WriteAllLines(_testFile, lines);
        }

        [Fact]
        public async Task BuildAsync_ShouldExtractTimeAndOffsets()
        {
            var index = await ReplayIndex.BuildAsync(_testFile);
            
            Assert.Equal(3, index.Entries.Count);
            Assert.Equal(100, index.Entries[0].TimeMs);
            Assert.Equal(200, index.Entries[1].TimeMs);
            Assert.Equal(350, index.Entries[2].TimeMs);
            
            // Validate limits
            Assert.Equal(100, index.StartTimeMs);
            Assert.Equal(350, index.EndTimeMs);
            Assert.Equal(250, index.DurationMs);

            // Read using the discovered offset
            using var fs = new FileStream(_testFile, FileMode.Open, FileAccess.Read);
            fs.Seek(index.Entries[1].ByteOffset, SeekOrigin.Begin);
            
            using var reader = new StreamReader(fs);
            string line2 = await reader.ReadLineAsync();
            
            Assert.Contains("\"t\":200", line2);
        }

        [Fact]
        public async Task GetClosestOffset_ShouldReturnBinarySearchResult()
        {
            var index = await ReplayIndex.BuildAsync(_testFile);

            long exactMatch = index.GetClosestOffset(200);
            Assert.Equal(index.Entries[1].ByteOffset, exactMatch);

            long beforeFirst = index.GetClosestOffset(50);
            Assert.Equal(index.Entries[0].ByteOffset, beforeFirst);

            long inBetween = index.GetClosestOffset(299);
            Assert.Equal(index.Entries[1].ByteOffset, inBetween);

            long afterLast = index.GetClosestOffset(500);
            Assert.Equal(index.Entries[2].ByteOffset, afterLast);
        }
        
        [Fact]
        public async Task ReplayRunner_Seek_ShouldStartFromOffset()
        {
            var index = await ReplayIndex.BuildAsync(_testFile);
            long seekOffsetBytes = index.GetClosestOffset(200);

            var runner = new ReplayRunner(_testFile);
            int eventCount = 0;
            long firstInterceptTime = -1;

            await runner.RunAsync(
                onDataCallback: (data) =>
                {
                    eventCount++;
                    return Task.CompletedTask;
                },
                onTimeUpdate: (ms) =>
                {
                    if (firstInterceptTime == -1) firstInterceptTime = ms;
                    return Task.CompletedTask;
                },
                startOffsetBytes: seekOffsetBytes,
                minTimeMs: 0
            );

            // Excludes t:100 since we sought to t:200
            Assert.Equal(2, eventCount); 
            Assert.Equal(200, firstInterceptTime);
        }

        public void Dispose()
        {
            if (File.Exists(_testFile))
            {
                File.Delete(_testFile);
            }
        }
    }
}
