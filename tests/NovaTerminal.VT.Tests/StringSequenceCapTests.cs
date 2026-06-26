using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// Regression tests for #106: OSC/DCS/APC string accumulation must be bounded so a hostile
// or runaway stream cannot grow parser buffers without limit. The production cap is generous
// (it must not truncate legitimate inline images carried over these sequences); these tests
// lower MaxStringSequenceChars so they exercise the cap with tiny payloads.
public class StringSequenceCapTests
{
    private const int TestCap = 1000;

    [Fact]
    public void Osc_string_accumulation_is_capped_and_parser_recovers()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 1);
        var parser = new AnsiParser(buffer) { MaxStringSequenceChars = TestCap };

        string? title = null;
        parser.OnTitleChanged = t => title = t;

        // An OSC 0 (window title) whose payload exceeds the cap. Without the guard the
        // parser would accumulate every byte; with it, accumulation stops at the cap.
        int overCap = TestCap + 100;
        string input = "\x1b]0;" + new string('A', overCap) + "\x07Z";

        parser.Process(input);

        Assert.NotNull(title);
        // Truncated: far fewer chars than were sent, and never above the cap.
        Assert.True(title!.Length < overCap, "title should be truncated below the payload length");
        Assert.True(title.Length <= TestCap, "title must not exceed the cap");

        // The parser must terminate the over-long sequence cleanly and resume normal
        // rendering — the trailing 'Z' lands at the home cell rather than being lost or
        // treated as part of the OSC string.
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("Z", buffer.GetGrapheme(col: 0, viewRow: 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Dcs_sixel_payload_is_capped()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 1);
        var parser = new AnsiParser(buffer) { MaxStringSequenceChars = TestCap };
        var decoder = new RecordingImageDecoder();
        parser.ImageDecoder = decoder;

        // DCS Sixel payload (begins with 'q') exceeding the cap.
        int overCap = TestCap + 100;
        parser.Process("\x1bPq" + new string('~', overCap) + "\x07");

        Assert.NotNull(decoder.LastSixelPayload);
        Assert.True(decoder.LastSixelPayload!.Length < overCap, "sixel payload should be truncated");
        Assert.True(decoder.LastSixelPayload.Length <= TestCap, "sixel payload must not exceed the cap");
    }

    [Fact]
    public void Kitty_payload_exceeding_cap_across_chunks_is_discarded_not_decoded()
    {
        var buffer = new TerminalBuffer(cols: 80, rows: 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { MaxStringSequenceChars = TestCap };
        var decoder = new RecordingImageDecoder();
        parser.ImageDecoder = decoder;

        // Chunked Kitty transfer (m=1 continuations) whose accumulated payload blows past
        // the cap before the final m=0 chunk arrives.
        string chunk = new string('Q', 600);
        parser.Process("\x1b_Ga=T,f=100,m=1;" + chunk + "\x07"); // 600 -> under cap
        parser.Process("\x1b_Gm=1;" + chunk + "\x07");           // 1200 -> trips overflow
        parser.Process("\x1b_Gm=0;QUJD\x07");                    // terminator

        // The truncated image must be discarded outright — the decoder is never invoked
        // (no CPU spent base64-decoding a corrupt multi-chunk payload).
        Assert.Equal(0, decoder.DecodeImageBytesCallCount);

        // State is reset, so a subsequent well-formed image decodes normally.
        parser.Process("\x1b_Ga=T,f=100,m=0;QUJD\x07");
        Assert.Equal(1, decoder.DecodeImageBytesCallCount);
    }

    [Fact]
    public void Normal_sized_sequences_are_unaffected_by_cap()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 1);
        var parser = new AnsiParser(buffer) { MaxStringSequenceChars = TestCap };
        var decoder = new RecordingImageDecoder();
        parser.ImageDecoder = decoder;

        string? title = null;
        parser.OnTitleChanged = t => title = t;

        parser.Process("\x1b]0;hello\x07");
        parser.Process("\x1bPq#0!1~\x07");

        Assert.Equal("hello", title);
        Assert.Equal("q#0!1~", decoder.LastSixelPayload);
    }

    private sealed class RecordingImageDecoder : IImageDecoder
    {
        public string? LastSixelPayload { get; private set; }
        public int DecodeImageBytesCallCount { get; private set; }

        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            DecodeImageBytesCallCount++;
            pixelWidth = 1;
            pixelHeight = 1;
            return new object();
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
        {
            LastSixelPayload = sixelData;
            pixelWidth = 0;
            pixelHeight = 0;
            return null;
        }
    }
}
