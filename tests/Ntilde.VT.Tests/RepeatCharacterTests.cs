using System.Text;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// REP — <c>CSI Ps b</c>, ECMA-48 §8.3.103 — repeats the preceding graphic character.
/// </summary>
/// <remarks>
/// Not a nicety: we advertise <c>TERM=xterm-256color</c>, whose terminfo declares
/// <c>rep=%p1%c\E[%p2%{1}%-%db</c>, so ncurses on the far end of an SSH session draws every run of
/// repeated characters this way — box borders, rules, padding, meter bars. While REP was
/// unimplemented those runs rendered as a single character, and each one also fell through to the
/// parser's "Unhandled CSI" diagnostic, which the debug log wrote to disk synchronously.
/// </remarks>
public class RepeatCharacterTests
{
    private static string RowText(TerminalBuffer buffer, int row)
    {
        var sb = new StringBuilder();
        var cells = buffer.ViewportRows[row].Cells;
        for (int c = 0; c < buffer.Cols; c++)
        {
            sb.Append(cells[c].Character == '\0' ? ' ' : cells[c].Character);
        }
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void Rep_DrawsTheRunNcursesAsksFor()
    {
        var buffer = new TerminalBuffer(cols: 80, rows: 6);
        var parser = new AnsiParser(buffer);

        // Exactly the shape terminfo's `rep` produces: print the character, then repeat it.
        parser.Process("\x1b[H-\x1b[39b");

        Assert.Equal(new string('-', 40), RowText(buffer, 0));
    }

    [Theory]
    [InlineData("\x1b[b", "xx")]      // omitted parameter defaults to 1
    [InlineData("\x1b[0b", "xx")]     // Ps=0 is Ps=1
    [InlineData("\x1b[1b", "xx")]
    [InlineData("\x1b[4b", "xxxxx")]
    public void Rep_ParameterDefaultsToOne(string sequence, string expected)
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[Hx" + sequence);

        Assert.Equal(expected, RowText(buffer, 0));
    }

    [Fact]
    public void Rep_RepeatsOnlyTheLastCharacterOfTheRun()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[Hab\x1b[3b");

        Assert.Equal("abbbb", RowText(buffer, 0));
    }

    [Fact]
    public void Rep_RepeatsTheMappedGlyph_NotTheWireByte()
    {
        // DEC special graphics: 'q' on the wire is U+2500. The run has to be of what is painted,
        // which is the case ncurses uses `rep` for most.
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[H\x1b(0q\x1b[9b\x1b(B");

        Assert.Equal(new string('─', 10), RowText(buffer, 0));
    }

    [Theory]
    // A control ends the run of graphic characters...
    [InlineData("\x1b[Hx\r\x1b[5b")]
    [InlineData("\x1b[Hx\b\x1b[5b")]
    // ...and so does any other control sequence.
    [InlineData("\x1b[Hx\x1b[1;3H\x1b[5b")]
    [InlineData("\x1b[Hx\x1b[0m\x1b[5b")]
    // Nothing printed at all: nothing to repeat.
    [InlineData("\x1b[H\x1b[5b")]
    public void Rep_IsANoOpWhenThereIsNoPrecedingGraphicCharacter(string sequence)
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process(sequence);

        // A stale repeat is worse than no repeat: it paints characters the stream never sent.
        Assert.DoesNotContain("xx", RowText(buffer, 0));
    }

    [Fact]
    public void Rep_SurvivesItsOwnSequence_SoConsecutiveRepsBothApply()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[Hx\x1b[2b\x1b[2b");

        Assert.Equal("xxxxx", RowText(buffer, 0));
    }

    [Theory]
    [InlineData("\x1b[?5b")]   // private leader: a different sequence
    [InlineData("\x1b[>5b")]
    [InlineData("\x1b[5 b")]   // intermediate: likewise
    public void Rep_RejectsLeaderAndIntermediateForms(string sequence)
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[Hx" + sequence);

        Assert.Equal("x", RowText(buffer, 0));
    }

    [Fact]
    public void Rep_ClampsTheCountToOneLine()
    {
        // The count is an amplification factor — five bytes in, MaxCsiParamValue cells out — so it
        // is bounded at a line's worth. Without the clamp this scrolls ~800 rows of transcript.
        var buffer = new TerminalBuffer(cols: 80, rows: 6);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[Hz\x1b[65535b");

        Assert.Equal(0, buffer.Scrollback.Count);
        Assert.Equal(new string('z', 80), RowText(buffer, 0));
    }

    [Fact]
    public void Rep_RepeatsAWideCharacterAtItsFullWidth()
    {
        // A double-width character occupies two columns per repetition. The repeat goes through
        // the ordinary write path, so the width handling is the buffer's and this only pins that
        // REP does not bypass it — five repeats of a 2-column glyph must land the cursor at 10.
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[H中\x1b[4b");

        Assert.Equal(10, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);

        // Five lead cells, each followed by its continuation cell.
        var cells = buffer.ViewportRows[0].Cells;
        for (int i = 0; i < 10; i += 2)
        {
            Assert.Equal('中', cells[i].Character);
        }
    }

    [Fact]
    public void Rep_WrapsAtTheRightMargin_WhenAutowrapIsOn()
    {
        // DECAWM on (the default): a run that outgrows the line continues on the next one, and the
        // row is marked wrapped so reflow keeps the two halves together.
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[H" + new string('.', 18) + "X\x1b[5b");

        Assert.True(buffer.ViewportRows[0].IsWrapped);
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
        Assert.Equal("..................XX", RowText(buffer, 0));
        Assert.Equal("XXXX", RowText(buffer, 1));
    }

    [Fact]
    public void Rep_StopsAtTheRightMargin_WhenAutowrapIsOff()
    {
        // DECAWM off (CSI ?7l): the run is clipped at the margin rather than continuing onto the
        // next row. The repeat must respect the mode, not paint past it.
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[?7l");
        parser.Process("\x1b[H" + new string('.', 18) + "X\x1b[5b");

        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal("..................XX", RowText(buffer, 0));
        Assert.Equal(string.Empty, RowText(buffer, 1));
    }

    [Fact]
    public void Rep_DoesNotRepeatHalfOfAGrapheme()
    {
        // A surrogate pair is one character; repeating the trailing UTF-16 unit would emit
        // lone low surrogates. Fail closed: no repeat at all.
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[H\U0001F600\x1b[3b");

        Assert.DoesNotContain("\udc00", RowText(buffer, 0));
    }
}
