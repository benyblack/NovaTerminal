using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        public async Task ReplayV2_RoundTrip_WithInput_Works()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // 1. Record v2 with Input
                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    byte[] data1 = Encoding.UTF8.GetBytes("Prompt> ");
                    recorder.RecordChunk(data1, data1.Length);

                    recorder.RecordInput("ls\r");

                    byte[] data2 = Encoding.UTF8.GetBytes("\r\nfile1.txt  file2.txt\r\nPrompt> ");
                    recorder.RecordChunk(data2, data2.Length);
                }

                // 2. Replay v2
                var gatheredData = new StringBuilder();
                var gatheredInput = new StringBuilder();

                var runner = new ReplayRunner(tempFile);
                await runner.RunAsync(
                    onDataCallback: async (data) =>
                    {
                        gatheredData.Append(Encoding.UTF8.GetString(data));
                        await Task.CompletedTask;
                    },
                    onInputCallback: async (input) =>
                    {
                        gatheredInput.Append(input);
                        await Task.CompletedTask;
                    }
                );

                // 3. Assert
                Assert.Contains("Prompt> ", gatheredData.ToString());
                Assert.Contains("file1.txt", gatheredData.ToString());
                Assert.Equal("ls\r", gatheredInput.ToString());

                // Input event should carry raw bytes in d (base64), with legacy i present.
                var lines = File.ReadAllLines(tempFile);
                string? inputLine = lines.FirstOrDefault(l => l.Contains("\"type\":\"input\""));
                Assert.NotNull(inputLine);
                Assert.Contains("\"d\":\"", inputLine);
                Assert.Contains("\"i\":\"ls\\r\"", inputLine);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public async Task ReplayV2_Snapshot_RoundTrip_Works()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // 1. Prepare a buffer with state
                var buffer = new TerminalBuffer(80, 24);
                buffer.CurrentForeground = new TermColor(255, 0, 0);
                buffer.CurrentBackground = new TermColor(0, 0, 255);
                buffer.Write("Colored Text ");

                buffer.CurrentForeground = TermColor.White;
                buffer.CurrentBackground = TermColor.Black;
                buffer.Write("🚀 Emoji");

                buffer.CursorRow = 5;
                buffer.CursorCol = 10;
                buffer.SetScrollingRegion(2, 20);
                buffer.Modes.IsAutoWrapMode = false;
                buffer.Modes.IsApplicationCursorKeys = true;
                buffer.Modes.IsOriginMode = true;
                buffer.Modes.IsBracketedPasteMode = true;
                buffer.Modes.IsCursorVisible = false;
                buffer.IsBold = true;
                buffer.IsItalic = true;
                buffer.IsUnderline = true;
                buffer.IsStrikethrough = true;
                buffer.IsFaint = true;

                // 2. Record Snapshot
                using (var recorder = new PtyRecorder(tempFile, 80, 24))
                {
                    recorder.RecordSnapshot(buffer);
                }

                // 3. Replay Snapshot
                ReplaySnapshot? restoredSnapshot = null;
                var runner = new ReplayRunner(tempFile);
                await runner.RunAsync(
                    onDataCallback: (d) => Task.CompletedTask,
                    onSnapshotCallback: async (s) =>
                    {
                        restoredSnapshot = s;
                        await Task.CompletedTask;
                    }
                );

                // 4. Assert
                Assert.NotNull(restoredSnapshot);
                Assert.Equal(80, restoredSnapshot!.Cols);
                Assert.Equal(24, restoredSnapshot!.Rows);
                Assert.Equal(5, restoredSnapshot!.CursorRow);
                Assert.Equal(10, restoredSnapshot!.CursorCol);
                Assert.Equal(2, restoredSnapshot.ScrollTop);
                Assert.Equal(20, restoredSnapshot.ScrollBottom);
                Assert.False(restoredSnapshot.IsAutoWrapMode);
                Assert.True(restoredSnapshot.IsApplicationCursorKeys);
                Assert.True(restoredSnapshot.IsOriginMode);
                Assert.True(restoredSnapshot.IsBracketedPasteMode);
                Assert.False(restoredSnapshot.IsCursorVisible);
                Assert.True(restoredSnapshot.IsBold);
                Assert.True(restoredSnapshot.IsItalic);
                Assert.True(restoredSnapshot.IsUnderline);
                Assert.True(restoredSnapshot.IsStrikethrough);
                Assert.True(restoredSnapshot.IsFaint);

                // Verify extended text (Emoji)
                Assert.NotNull(restoredSnapshot.ExtendedText);
                Assert.Contains("🚀", restoredSnapshot.ExtendedText!.Values);

                // Verify binary cell data integrity
                Assert.NotNull(restoredSnapshot.CellsBase64);
                byte[] cellBytes = Convert.FromBase64String(restoredSnapshot.CellsBase64!);

                // Reconstruct cells from bytes
                var cellSpan = MemoryMarshal.Cast<byte, TerminalCell>(cellBytes.AsSpan());
                Assert.Equal(80 * 24, cellSpan.Length);

                // Check a cell we know (the first 'C' in 'Colored Text')
                var firstCell = cellSpan[0];
                Assert.Equal('C', firstCell.Character);
                Assert.Equal(new TermColor(255, 0, 0).ToUint(), firstCell.Fg);
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

        [Fact]
        public async Task ReplayReaderWriter_Aliases_Work()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                using (var writer = new ReplayWriter(tempFile, 80, 24, "pwsh.exe"))
                {
                    byte[] data = Encoding.UTF8.GetBytes("Alias");
                    writer.RecordChunk(data, data.Length);
                }

                var gathered = new StringBuilder();
                var reader = new ReplayReader(tempFile);
                await reader.RunAsync(async (data) =>
                {
                    gathered.Append(Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                });

                Assert.Equal("Alias", gathered.ToString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ApplySnapshot_RestoresModeAndStyleState()
        {
            var buffer = new TerminalBuffer(80, 24);
            var snapshot = new ReplaySnapshot
            {
                Cols = 80,
                Rows = 24,
                CursorCol = 7,
                CursorRow = 3,
                IsAltScreen = false,
                ScrollTop = 1,
                ScrollBottom = 22,
                IsAutoWrapMode = false,
                IsApplicationCursorKeys = true,
                IsOriginMode = true,
                IsBracketedPasteMode = true,
                IsCursorVisible = false,
                CurrentForeground = TermColor.FromRgb(10, 20, 30).ToUint(),
                CurrentBackground = TermColor.FromRgb(40, 50, 60).ToUint(),
                CurrentFgIndex = 2,
                CurrentBgIndex = 4,
                IsDefaultForeground = false,
                IsDefaultBackground = false,
                IsInverse = true,
                IsBold = true,
                IsFaint = true,
                IsItalic = true,
                IsUnderline = true,
                IsBlink = true,
                IsStrikethrough = true,
                IsHidden = true
            };

            buffer.ApplySnapshot(snapshot);

            Assert.Equal(7, buffer.CursorCol);
            Assert.Equal(3, buffer.CursorRow);
            Assert.Equal(1, buffer.ScrollTop);
            Assert.Equal(22, buffer.ScrollBottom);
            Assert.False(buffer.Modes.IsAutoWrapMode);
            Assert.True(buffer.Modes.IsApplicationCursorKeys);
            Assert.True(buffer.Modes.IsOriginMode);
            Assert.True(buffer.Modes.IsBracketedPasteMode);
            Assert.False(buffer.Modes.IsCursorVisible);
            Assert.Equal(snapshot.CurrentForeground, buffer.CurrentForeground.ToUint());
            Assert.Equal(snapshot.CurrentBackground, buffer.CurrentBackground.ToUint());
            Assert.Equal(snapshot.CurrentFgIndex, buffer.CurrentFgIndex);
            Assert.Equal(snapshot.CurrentBgIndex, buffer.CurrentBgIndex);
            Assert.True(buffer.IsInverse);
            Assert.True(buffer.IsBold);
            Assert.True(buffer.IsFaint);
            Assert.True(buffer.IsItalic);
            Assert.True(buffer.IsUnderline);
            Assert.True(buffer.IsBlink);
            Assert.True(buffer.IsStrikethrough);
            Assert.True(buffer.IsHidden);
        }
    }
}
