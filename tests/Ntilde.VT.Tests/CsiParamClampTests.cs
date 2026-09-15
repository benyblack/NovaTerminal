using System.Diagnostics;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// Regression for the DoS the #124 fuzzer found: a CSI sequence with an enormous numeric
// parameter (e.g. CSI 333333261 S — scroll up 333 million lines) made the parser loop for tens
// of seconds. Parameters are now clamped at parse time, so these complete near-instantly.
public class CsiParamClampTests
{
    [Theory]
    [InlineData("\x1b[333333261S")]   // Scroll Up
    [InlineData("\x1b[999999999T")]   // Scroll Down
    [InlineData("\x1b[888888888b")]   // Repeat preceding char
    [InlineData("\x1b[777777777L")]   // Insert Lines
    [InlineData("\x1b[666666666M")]   // Delete Lines
    [InlineData("\x1b[555555555@")]   // Insert Chars
    [InlineData("\x1b[444444444P")]   // Delete Chars
    [InlineData("\x1b[2147483999;2147483999H")] // huge CUP row;col (also past int.MaxValue)
    public void HugeCsiParameter_DoesNotHang(string sequence)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        // Prime a char so Repeat (b) has something to repeat.
        parser.Process("X");

        var sw = Stopwatch.StartNew();
        parser.Process(sequence);
        sw.Stop();

        // The unclamped bug took ~35s; clamped it is sub-millisecond. A 2s ceiling is a huge
        // margin that still fails loudly if a parameter ever goes unbounded again.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"CSI '{sequence}' took {sw.ElapsedMilliseconds}ms — parameter likely unclamped");
    }

    [Fact]
    public void NormalCsiParameters_StillWork()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        // CUP to row 5, col 10 (1-based) → cursor at (4,9) 0-based.
        parser.Process("\x1b[5;10H");

        Assert.Equal(4, buffer.CursorRow);
        Assert.Equal(9, buffer.CursorCol);
    }
}
