using System.IO;
using System.Text;
using System.Threading.Tasks;
using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests;

public sealed class ScrollAndWrapReplayTests
{
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_BottomMarginPendingWrap_ScrollsWithinRegion()
    {
        string recPath = Path.Combine(Path.GetTempPath(), $"nova-scroll-wrap-{Path.GetRandomFileName()}.rec");

        try
        {
            using (var recorder = new PtyRecorder(recPath, 3, 4))
            {
                Record(recorder, "aaa\r\nbbb\r\nccc\r\nddd");
                Record(recorder, "\x1b[2;4r");
                Record(recorder, "\x1b[4;3HXY");
            }

            var buffer = new TerminalBuffer(3, 4);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async data =>
            {
                parser.Process(Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

            Assert.Equal("aaa", GetRowText(buffer, 0));
            Assert.Equal("ccc", GetRowText(buffer, 1));
            Assert.Equal("ddX", GetRowText(buffer, 2));
            Assert.Equal("Y", GetRowText(buffer, 3));
            Assert.Equal(1, buffer.CursorCol);
            Assert.Equal(3, buffer.CursorRow);
            Assert.False(snapshot.IsAltScreen);
            Assert.Equal(1, snapshot.CursorCol);
            Assert.Equal(3, snapshot.CursorRow);
            Assert.Equal("aaa", snapshot.Lines[0]);
            Assert.Equal("ccc", snapshot.Lines[1]);
            Assert.Equal("ddX", snapshot.Lines[2]);
            Assert.Equal("Y", snapshot.Lines[3].TrimEnd());
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

    private static string GetRowText(TerminalBuffer buffer, int row)
    {
        char[] chars = new char[buffer.ViewportRows[row].Cells.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            char cell = buffer.ViewportRows[row].Cells[i].Character;
            chars[i] = cell == '\0' ? ' ' : cell;
        }

        return new string(chars).TrimEnd();
    }
}
