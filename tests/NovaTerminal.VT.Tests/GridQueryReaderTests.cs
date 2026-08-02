using System.Collections.Generic;
using System.Linq;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// The grid-truth query reader: the text between the last <c>OSC 133;B</c> mark and the cursor.
/// </summary>
/// <remarks>
/// Command Assist V2 deletes its keystroke shadow buffer on the strength of these tests, so the
/// bar is "what does the grid actually contain", not "what did we type". Everything is driven
/// through the real parser and write path rather than by poking cells, because the properties
/// under test — wrap flags, wide-cell continuations, deferred autowrap, scrollback paging — are
/// produced by that path and nowhere else.
/// </remarks>
public class GridQueryReaderTests
{
    private const string PromptEnd = "\x1b]133;B\x07";
    private const string PromptStart = "\x1b]133;A\x07";

    // Written as escapes rather than literals so the assertions cannot depend on the source
    // file's encoding. U+4F60 U+597D = two double-width CJK ideographs; U+1F44D = an emoji
    // (surrogate pair, double width, stored in the row's extended-text side table).
    private const string CjkOne = "你";
    private const string Cjk = "你好";
    private const string Emoji = "👍";

    private sealed class Session
    {
        private readonly List<ShellIntegrationMark> _marks = new();

        public Session(int cols = 40, int rows = 6, int? maxHistory = null)
        {
            Buffer = new TerminalBuffer(cols, rows);
            if (maxHistory is int history)
            {
                // The byte budget is derived from MaxHistory and eviction works in whole
                // 64-row pages, so 128 is the smallest budget that both retains history and
                // actually evicts.
                Buffer.MaxHistory = history;
            }

            Parser = new AnsiParser(Buffer);
            Parser.OnCommandStarted = mark => _marks.Add(mark);
        }

        public TerminalBuffer Buffer { get; }

        public AnsiParser Parser { get; }

        public List<ShellIntegrationMark> Marks => _marks;

        /// <summary>The newest mark — a prompt repaint re-emits B, and the newest one is truth.</summary>
        public ShellIntegrationMark Mark => _marks[^1];

        public Session Write(string data)
        {
            Parser.Process(data);
            return this;
        }

        /// <summary>Prompt as every bootstrap paints it: A, the prompt text, then B.</summary>
        public Session Prompt(string text = "$ ") => Write(PromptStart + text + PromptEnd);

        public bool TryRead(out GridCommandLine line)
            => GridQueryReader.TryReadCommandLine(Buffer, Mark, out line);

        public GridCommandLine Read()
        {
            Assert.True(TryRead(out var line), "the mark should have resolved to a readable span");
            return line;
        }
    }

    // ---------------------------------------------------------------- simple reads

    [Fact]
    public void SimpleLine_IsReadFromTheMarkToTheCursor()
    {
        var s = new Session().Prompt().Write("git status");

        var line = s.Read();

        Assert.Equal("git status", line.Text);
        Assert.Equal(10, line.CursorOffset);
        Assert.False(line.IsMultiline);
        Assert.False(line.RightPromptTrimmed);
        Assert.Equal(0, line.StartRow);
        Assert.Equal(0, line.EndRow);
    }

    [Fact]
    public void EmptyInput_ReadsAnEmptyLineRatherThanFailing()
    {
        // The mark lands on the cursor cell, so "nothing typed yet" is a *successful* read of
        // the empty string. Command Assist needs to tell that apart from "no usable mark".
        var s = new Session().Prompt();

        var line = s.Read();

        Assert.Equal(string.Empty, line.Text);
        Assert.Equal(0, line.CursorOffset);
    }

    [Fact]
    public void PromptTextItselfIsNeverIncluded()
    {
        var s = new Session().Prompt("user@host ~/src (main) $ ").Write("ls");

        Assert.Equal("ls", s.Read().Text);
    }

    [Fact]
    public void CursorMidLine_AfterLeftArrows_KeepsTheWholeTextAndMovesTheOffset()
    {
        var s = new Session().Prompt().Write("git status").Write("\x1b[6D");

        var line = s.Read();

        Assert.Equal("git status", line.Text);
        Assert.Equal(4, line.CursorOffset);
    }

    [Fact]
    public void CursorAtStartOfInput_AfterCtrlA_ReadsOffsetZero()
    {
        // Ctrl+A reaches the terminal as an absolute column move to the first input cell.
        var s = new Session().Prompt().Write("git status").Write("\x1b[3G");

        var line = s.Read();

        Assert.Equal("git status", line.Text);
        Assert.Equal(0, line.CursorOffset);
    }

    [Fact]
    public void TrailingSpacesLeftOfTheCursorAreKept()
    {
        // "git " with the cursor parked after the space: the space is typed input, and
        // dropping it would make every "complete the next argument" suggestion wrong.
        var s = new Session().Prompt().Write("git ");

        var line = s.Read();

        Assert.Equal("git ", line.Text);
        Assert.Equal(4, line.CursorOffset);
    }

    [Fact]
    public void BlankCellsBeyondBothTheCursorAndTheContentAreDropped()
    {
        var s = new Session().Prompt().Write("ls").Write("\x1b[4G");

        var line = s.Read();

        Assert.Equal("ls", line.Text); // not "ls" plus 37 columns of blanks
        Assert.Equal(1, line.CursorOffset);
    }

    [Fact]
    public void HistoryRecall_IsReadFromTheGridEvenThoughNoKeystrokesProducedIt()
    {
        // The desync class that killed V1: the shell repaints the line wholesale (Up arrow,
        // Ctrl+U, tab completion) and the terminal only ever sees the redraw.
        var s = new Session().Prompt().Write("gi");
        s.Write("\r\x1b[K").Write("$ git log --oneline -20"); // shell redraws prompt + recalled line

        Assert.Equal("git log --oneline -20", s.Read().Text);
    }

    // ---------------------------------------------------------------- soft wrap

    [Fact]
    public void SoftWrapAcrossTwoRows_IsOneLogicalLineWithNoInjectedNewline()
    {
        string input = new string('a', 18) + new string('b', 12);
        var s = new Session(cols: 20, rows: 6).Prompt().Write(input);

        var line = s.Read();

        Assert.Equal(input, line.Text);
        Assert.DoesNotContain("\n", line.Text);
        Assert.False(line.IsMultiline);
        Assert.Equal(30, line.CursorOffset);
        Assert.Equal(0, line.StartRow);
        Assert.Equal(1, line.EndRow);
    }

    [Fact]
    public void SoftWrapAcrossThreeRows_IsStillOneLogicalLine()
    {
        string input = new string('a', 14) + new string('b', 16) + new string('c', 10);
        var s = new Session(cols: 16, rows: 6).Prompt().Write(input);

        var line = s.Read();

        Assert.Equal(input, line.Text);
        Assert.Equal(40, line.CursorOffset);
        Assert.Equal(2, line.EndRow);
    }

    [Theory]
    // Cursor parked on each physical row of a three-row wrapped line. The span must still be
    // the whole logical line: soft-wrapped continuations are followed *past* the cursor row.
    [InlineData(1, 5, 2)]   // row 0, column 4 -> two cells into the input
    [InlineData(2, 3, 16)]  // row 1
    [InlineData(3, 7, 36)]  // row 2
    public void WrappedLine_CursorOnAnyRow_KeepsTheWholeLineAndMapsTheOffset(
        int cupRow, int cupCol, int expectedOffset)
    {
        string input = new string('a', 14) + new string('b', 16) + new string('c', 10);
        var s = new Session(cols: 16, rows: 6).Prompt().Write(input);
        s.Write($"\x1b[{cupRow};{cupCol}H");

        var line = s.Read();

        Assert.Equal(input, line.Text);
        Assert.Equal(expectedOffset, line.CursorOffset);
        Assert.Equal(2, line.EndRow);
    }

    [Fact]
    public void InputEndingExactlyAtTheRightEdge_IsWholeDespiteTheDeferredWrap()
    {
        // Deferred autowrap: the cursor is parked on the last column with the wrap pending, so
        // its text position is one *past* it. Reading the raw cursor column would lose the
        // final character and put the offset in the wrong place.
        string input = new string('a', 17) + "z";
        var s = new Session(cols: 20, rows: 6).Prompt().Write(input);

        Assert.True(s.Buffer.IsPendingWrap);

        var line = s.Read();

        Assert.Equal(input, line.Text);
        Assert.Equal(18, line.CursorOffset);
        Assert.Equal(0, line.EndRow);
    }

    [Fact]
    public void OneCharacterPastTheRightEdge_WrapsOntoTheNextRow()
    {
        string input = new string('a', 17) + "zy";
        var s = new Session(cols: 20, rows: 6).Prompt().Write(input);

        var line = s.Read();

        Assert.Equal(input, line.Text);
        Assert.Equal(19, line.CursorOffset);
        Assert.Equal(1, line.EndRow);
    }

    // ---------------------------------------------------------------- multiline

    [Fact]
    public void MultilineContinuation_IsRawTextWithNewlinesAndTheMultilineFlag()
    {
        // Decision (b): the span is returned raw, including whatever the shell painted as a
        // continuation prompt. Nothing marks PS2 cells as prompt rather than input, so the
        // reader cannot strip them and must instead flag the text as untrustworthy-as-prefix.
        var s = new Session()
            .Prompt()
            .Write("for i in 1 2 3")
            .Write("\r\n> do echo $i");

        var line = s.Read();

        Assert.Equal("for i in 1 2 3\n> do echo $i", line.Text);
        Assert.True(line.IsMultiline);
        Assert.Equal(line.Text.Length, line.CursorOffset);
        Assert.Equal(1, line.EndRow);
    }

    [Fact]
    public void MultilineContinuation_CursorOnAnEarlierLine_ReadsThatLineAloneAndFlagsNothing()
    {
        // Documented limitation, pinned here so a change to it is deliberate: the span stops at
        // the end of the cursor's logical line. Extending across hard breaks whenever the row
        // below has content was rejected because zsh prints its completion listing right there,
        // which would misfire on every tab completion.
        var s = new Session()
            .Prompt()
            .Write("for i in 1 2 3")
            .Write("\r\n> do echo $i")
            .Write("\x1b[1;6H"); // arrow up into the first line

        var line = s.Read();

        Assert.Equal("for i in 1 2 3", line.Text);
        Assert.False(line.IsMultiline);
        Assert.Equal(3, line.CursorOffset);
    }

    [Fact]
    public void MultilineSpanCombinesHardBreaksAndSoftWraps()
    {
        var s = new Session(cols: 16, rows: 6)
            .Prompt()
            .Write(new string('a', 14) + new string('b', 6)) // wraps onto a second row
            .Write("\r\n> tail");

        var line = s.Read();

        Assert.Equal(new string('a', 14) + new string('b', 6) + "\n> tail", line.Text);
        Assert.True(line.IsMultiline);
    }

    // ---------------------------------------------------------------- prompt redraw

    [Fact]
    public void PromptRedraw_TheNewestMarkIsTheOneThatCounts()
    {
        var s = new Session();
        s.Prompt().Write("stale text");
        s.Write("\r\x1b[K").Prompt("nova> ").Write("ls -la");

        Assert.Equal(2, s.Marks.Count);
        Assert.Equal("ls -la", s.Read().Text);

        // The first mark is still *live* (same generation, same row) but it starts two columns
        // earlier, inside the repainted prompt — which is exactly why the reader must be handed
        // the newest mark rather than caching one.
        Assert.True(GridQueryReader.TryReadCommandLine(s.Buffer, s.Marks[0], out var stale));
        Assert.NotEqual("ls -la", stale.Text);
    }

    // ---------------------------------------------------------------- scrollback and eviction

    [Fact]
    public void MarkRowInScrollback_StillReadsThroughThePagedRows()
    {
        // A multiline entry tall enough to push its own first row off the viewport. The mark
        // row now lives in paged scrollback, which has no TerminalRow object at all.
        var s = new Session(cols: 40, rows: 3)
            .Prompt()
            .Write("abc")
            .Write("\r\n> def")
            .Write("\r\n> ghi")
            .Write("\r\n> jkl");

        Assert.True(s.Buffer.Scrollback.Count > 0, "the first row must have scrolled off");

        var line = s.Read();

        Assert.Equal("abc\n> def\n> ghi\n> jkl", line.Text);
        Assert.Equal(0, line.StartRow);
        Assert.Equal(3, line.EndRow);
    }

    [Fact]
    public void MarkAgedOutOfScrollback_Fails()
    {
        var s = new Session(cols: 40, rows: 3, maxHistory: 128).Prompt().Write("ls");
        for (int i = 0; i < 200; i++) s.Write($"line {i}\r\n");

        Assert.True(s.Buffer.Scrollback.TotalRowsEvicted > 0, "the budget must actually evict");
        Assert.Equal(s.Buffer.Scrollback.Generation, s.Mark.Generation); // not a generation reset
        Assert.True(s.Mark.AbsoluteRow - s.Buffer.Scrollback.TotalRowsEvicted < 0);

        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void ScrollingAloneNeverInvalidatesAMark()
    {
        // Negative control for the eviction test: ordinary scrolling shifts row numbers but the
        // mark must keep resolving, or the reader would give up on every long command.
        var s = new Session(cols: 40, rows: 3, maxHistory: 128);
        for (int i = 0; i < 100; i++) s.Write($"line {i}\r\n");
        s.Prompt().Write("ls -la");

        var line = s.Read();

        Assert.Equal("ls -la", line.Text);
        Assert.True(s.Buffer.Scrollback.Count > 0);
    }

    // ---------------------------------------------------------------- invalidation

    [Fact]
    public void ClearedScrollback_BumpsTheGenerationAndTheMarkIsRefused()
    {
        // CSI 3J is what clear(1) sends with the E3 capability, so this is routine. It zeroes
        // both row counters, so the stale AbsoluteRow resolves to a plausible, in-range row
        // holding unrelated content: "a negative row means aged out" cannot catch it and only
        // the generation can. The mark is taken while it is still a viewport row, then pushed
        // into scrollback, so dropping the scrollback really does re-point it.
        var s = new Session(cols: 40, rows: 10);
        for (int i = 0; i < 5; i++) s.Write($"out {i}\r\n");
        s.Prompt().Write("ls");

        Assert.True(s.TryRead(out _));

        for (int i = 0; i < 20; i++) s.Write($"more {i}\r\n");
        s.Write("\x1b[3J");

        Assert.Equal(0L, s.Buffer.Scrollback.TotalRowsEvicted);
        Assert.InRange(s.Mark.AbsoluteRow, 0, s.Buffer.TotalLines - 1); // the trap
        Assert.NotEqual(s.Mark.Generation, s.Buffer.Scrollback.Generation);
        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void FullReset_IsRefused()
    {
        var s = new Session().Prompt().Write("ls");

        s.Buffer.Clear();

        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void ReflowingResize_IsRefused()
    {
        // A width change re-wraps every logical line and rebuilds the scrollback store, so the
        // mark's coordinates describe a layout that no longer exists. Benign in practice: every
        // shell repaints its prompt after a resize, and the repaint carries a fresh B.
        var s = new Session(cols: 40, rows: 6).Prompt().Write("git status");

        s.Buffer.Resize(24, 6);

        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void HeightOnlyResize_KeepsTheMarkUsable()
    {
        // Counterpart to the reflow case: no re-wrap, no coordinate-space reset. Rows pushed
        // from the viewport into scrollback keep their absolute identity.
        var s = new Session(cols: 40, rows: 6);
        for (int i = 0; i < 4; i++) s.Write($"line {i}\r\n");
        s.Prompt().Write("git status");

        s.Buffer.Resize(40, 3);

        Assert.Equal("git status", s.Read().Text);
    }

    [Fact]
    public void AltScreenMark_IsRefused()
    {
        var s = new Session();
        s.Write("\x1b[?1049h").Write("\x1b[H").Prompt("> ").Write("q");

        Assert.True(s.Mark.IsAltScreen);
        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void MainScreenMark_IsRefusedWhileTheAltScreenIsActive()
    {
        var s = new Session().Prompt().Write("vim");

        s.Write("\x1b[?1049h");

        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void CursorAboveTheMark_IsRefused()
    {
        var s = new Session().Write("out\r\nput\r\n").Prompt().Write("ls");

        s.Write("\x1b[1;1H");

        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void CursorLeftOfTheMarkOnTheMarkRow_IsRefused()
    {
        // The line editor moved the cursor into the prompt itself: the mark no longer describes
        // where input begins, so there is nothing truthful to return.
        var s = new Session().Prompt().Write("ls").Write("\x1b[1G");

        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void ImplausiblySpanningMark_IsRefused()
    {
        // The mark is only meaningful between B and the following C. Used past that window the
        // "command line" is really command output; the span cap stops the reader building a
        // multi-megabyte string out of it.
        var s = new Session(cols: 40, rows: 3).Prompt().Write("ls");
        s.Write(string.Concat(Enumerable.Repeat("\r\n", GridQueryReader.MaxSpanRows + 64)));

        Assert.Equal(s.Buffer.Scrollback.Generation, s.Mark.Generation);
        Assert.Equal(0L, s.Buffer.Scrollback.TotalRowsEvicted);
        Assert.False(s.TryRead(out _));
    }

    [Fact]
    public void NullBuffer_IsRefused()
    {
        Assert.False(GridQueryReader.TryReadCommandLine(null!, default, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(40)]
    [InlineData(4000)]
    public void MarkColumnOutOfRange_IsRefused(int column)
    {
        var buffer = new TerminalBuffer(40, 6);
        var mark = new ShellIntegrationMark(
            Row: 0, Column: column, AbsoluteRow: 0, IsAltScreen: false,
            Generation: buffer.Scrollback.Generation);

        Assert.False(GridQueryReader.TryReadCommandLine(buffer, mark, out _));
    }

    [Fact]
    public void MarkRowPastTheEndOfTheBuffer_IsRefused()
    {
        var buffer = new TerminalBuffer(40, 6);
        var mark = new ShellIntegrationMark(
            Row: 0, Column: 0, AbsoluteRow: 500, IsAltScreen: false,
            Generation: buffer.Scrollback.Generation);

        Assert.False(GridQueryReader.TryReadCommandLine(buffer, mark, out _));
    }

    // ---------------------------------------------------------------- right prompt

    [Fact]
    public void RightPrompt_IsExcluded()
    {
        // zsh RPROMPT / fish fish_right_prompt / starship's right prompt all paint right-aligned
        // text on the input's own row. The shape: a wide blank gap, then content flush against
        // the right edge (ZLE_RPROMPT_INDENT defaults to 1).
        var s = new Session(cols: 40, rows: 6)
            .Prompt()
            .Write("ls")
            .Write("\x1b[34G[main]") // right-aligned, ending one column short of the edge
            .Write("\x1b[5G");       // shell puts the cursor back at the end of the input

        var line = s.Read();

        Assert.Equal("ls", line.Text);
        Assert.Equal(2, line.CursorOffset);
        Assert.True(line.RightPromptTrimmed);
    }

    [Fact]
    public void RightPrompt_IsExcludedEvenWithTheCursorAtTheStartOfTheInput()
    {
        // Stopping at the cursor would be the easy rule and it is wrong: the cursor is mid-line
        // whenever the user has used an arrow key.
        var s = new Session(cols: 40, rows: 6)
            .Prompt()
            .Write("ls -la")
            .Write("\x1b[34G[main]")
            .Write("\x1b[3G");

        var line = s.Read();

        Assert.Equal("ls -la", line.Text);
        Assert.Equal(0, line.CursorOffset);
        Assert.True(line.RightPromptTrimmed);
    }

    [Fact]
    public void DoubleSpaceInsideTypedInput_IsNotMistakenForARightPrompt()
    {
        // The gap rule alone would cut "echo" off here. Two further conditions save it: the
        // trailing content must reach the right edge, and the gap must start at or after the
        // cursor.
        var s = new Session(cols: 40, rows: 6).Prompt().Write("echo  hi").Write("\x1b[3G");

        var line = s.Read();

        Assert.Equal("echo  hi", line.Text);
        Assert.False(line.RightPromptTrimmed);
    }

    [Fact]
    public void TrailingContentNotAtTheRightEdge_IsNotTreatedAsARightPrompt()
    {
        // Deliberately conservative boundary: a gap plus content that stops well short of the
        // edge is far more likely to be typed input than a right-aligned prompt, so it is kept.
        var s = new Session(cols: 40, rows: 6)
            .Prompt()
            .Write("ls")
            .Write("\x1b[20G[main]")
            .Write("\x1b[5G");

        var line = s.Read();

        Assert.EndsWith("[main]", line.Text);
        Assert.False(line.RightPromptTrimmed);
    }

    [Fact]
    public void SingleSpaceBeforeRightEdgeContent_IsNotTreatedAsARightPrompt()
    {
        // One space is a word separator, not a prompt gap.
        var s = new Session(cols: 12, rows: 6).Prompt().Write("ls abcdefgh");

        var line = s.Read();

        Assert.Equal("ls abcdefgh", line.Text);
        Assert.False(line.RightPromptTrimmed);
    }

    // ---------------------------------------------------------------- wide characters

    [Fact]
    public void DoubleWidthPromptCells_DoNotShiftTheStartOfInput()
    {
        var s = new Session().Prompt(Cjk + "$ ").Write("ls");

        Assert.Equal(6, s.Mark.Column); // two double-width cells, then "$ "
        Assert.Equal("ls", s.Read().Text);
    }

    [Fact]
    public void DoubleWidthInput_IsNotDoubleCountedAndItsContinuationIsNotEmitted()
    {
        var s = new Session().Prompt().Write("echo " + Cjk);

        var line = s.Read();

        Assert.Equal("echo " + Cjk, line.Text);
        Assert.Equal(7, line.CursorOffset); // characters, not columns
    }

    [Fact]
    public void EmojiInInput_SurvivesAsAWholeGrapheme()
    {
        var s = new Session().Prompt().Write("echo " + Emoji);

        var line = s.Read();

        Assert.Equal("echo " + Emoji, line.Text);
        Assert.Equal(7, line.CursorOffset); // 5 + the surrogate pair
    }

    [Fact]
    public void CursorBetweenDoubleWidthCharacters_MapsToACharacterBoundary()
    {
        var s = new Session().Prompt().Write(Cjk + "ls").Write("\x1b[7G"); // column 6 = the 'l'

        var line = s.Read();

        Assert.Equal(Cjk + "ls", line.Text);
        Assert.Equal(2, line.CursorOffset);
    }

    [Fact]
    public void WideCharacterThatDoesNotFitLeavesNoPhantomSpaceAtTheWrap()
    {
        // A double-width character with only one column left wraps early, leaving the last cell
        // of the row blank. That hole is layout, not input: emitting it would insert a space
        // into the middle of the command line.
        string ascii = new string('a', 16) + "x";
        var s = new Session(cols: 20, rows: 6).Prompt().Write(ascii + CjkOne);

        var line = s.Read();

        Assert.Equal(ascii + CjkOne, line.Text);
        Assert.DoesNotContain(" ", line.Text);
        Assert.Equal(1, line.EndRow);
    }

    [Fact]
    public void AdjacentWideCharactersAreNotReadAsARightPromptGap()
    {
        // The trailing half of a wide cell stores a space. Counting it as blank would let two
        // adjacent CJK characters look like the separator before a right-aligned prompt.
        var s = new Session(cols: 12, rows: 6).Prompt().Write(Cjk + Cjk);

        var line = s.Read();

        Assert.Equal(Cjk + Cjk, line.Text);
        Assert.False(line.RightPromptTrimmed);
    }

    // ---------------------------------------------------------------- lock discipline

    [Fact]
    public void ReadingWhileTheCallerAlreadyHoldsTheReadLock_DoesNotDeadlockOrThrow()
    {
        // The buffer lock is non-recursive, so the reader must notice an outer lock rather than
        // blindly re-entering. The render path holds one across whole frames.
        var s = new Session().Prompt().Write("ls");

        s.Buffer.Lock.EnterReadLock();
        try
        {
            Assert.True(GridQueryReader.TryReadCommandLine(s.Buffer, s.Mark, out var line));
            Assert.Equal("ls", line.Text);
        }
        finally
        {
            s.Buffer.Lock.ExitReadLock();
        }

        Assert.False(s.Buffer.Lock.IsReadLockHeld);
    }

    [Fact]
    public void TheReadLockIsReleasedOnBothOutcomes()
    {
        var s = new Session().Prompt().Write("ls");

        Assert.True(s.TryRead(out _));
        Assert.False(s.Buffer.Lock.IsReadLockHeld);

        s.Buffer.Clear();

        Assert.False(s.TryRead(out _));
        Assert.False(s.Buffer.Lock.IsReadLockHeld);
    }
}
