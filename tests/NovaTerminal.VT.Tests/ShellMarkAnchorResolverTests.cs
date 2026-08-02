using System.Collections.Generic;
using System.Linq;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// Mark-to-viewport-row resolution: the coordinate conversion Command Assist V2 anchors its
/// overlay on.
/// </summary>
/// <remarks>
/// The property under test is deliberately narrow — "is the marked prompt line on screen, and if
/// so which row" — because everything the overlay does downstream treats a returned row as fact.
/// Every case that cannot answer truthfully has to come back as "no anchor" so the caller falls
/// back to the geometric heuristic instead of placing against a wrong row. Driven through the real
/// parser and write path, like <see cref="GridQueryReaderTests"/>, so the scrollback and generation
/// behaviour under test is the production one.
/// </remarks>
public class ShellMarkAnchorResolverTests
{
    private const string PromptEnd = "\x1b]133;B\x07";

    private sealed class Session
    {
        private readonly List<ShellIntegrationMark> _marks = new();

        public Session(int cols = 40, int rows = 6, int? maxHistory = null)
        {
            Buffer = new TerminalBuffer(cols, rows);
            if (maxHistory is int history)
            {
                Buffer.MaxHistory = history;
            }

            Parser = new AnsiParser(Buffer);
            Parser.OnCommandStarted = mark => _marks.Add(mark);
        }

        public TerminalBuffer Buffer { get; }

        public AnsiParser Parser { get; }

        public ShellIntegrationMark Mark => _marks[^1];

        public Session Write(string data)
        {
            Parser.Process(data);
            return this;
        }

        public Session Prompt(string text = "$ ") => Write(text + PromptEnd);

        public bool TryResolve(out int visualRow, int scrollOffset = 0, int? visibleRows = null)
            => ShellMarkAnchorResolver.TryResolveVisualRow(
                Buffer,
                Mark,
                scrollOffset,
                visibleRows ?? Buffer.Rows,
                out visualRow);
    }

    [Fact]
    public void MarkOnTheFirstRow_ResolvesToRowZero()
    {
        var s = new Session().Prompt();

        Assert.True(s.TryResolve(out int row));
        Assert.Equal(0, row);
    }

    [Fact]
    public void MarkPartwayDownTheViewport_ResolvesToThatRow()
    {
        var s = new Session(rows: 6).Write("a\r\nb\r\nc\r\n").Prompt();

        Assert.True(s.TryResolve(out int row));
        Assert.Equal(3, row);
    }

    [Fact]
    public void MarkOnTheLastRow_ResolvesToTheLastRow()
    {
        var s = new Session(rows: 6).Write(string.Concat(Enumerable.Repeat("x\r\n", 5))).Prompt();

        Assert.True(s.TryResolve(out int row));
        Assert.Equal(5, row);
    }

    [Fact]
    public void MarkScrolledAboveTheViewport_HasNoAnchor()
    {
        // The prompt is pushed into scrollback by later output: still in history, no longer on
        // screen. Anchoring to it would put the bubble above the pane.
        var s = new Session(rows: 6).Prompt();
        s.Write(string.Concat(Enumerable.Repeat("out\r\n", 20)));

        Assert.True(s.Buffer.Scrollback.Count > 0, "the write must actually scroll");
        Assert.Equal(s.Buffer.Scrollback.Generation, s.Mark.Generation);
        Assert.False(s.TryResolve(out _));
    }

    [Fact]
    public void MarkScrolledAboveTheViewport_ComesBackWhenScrolledIntoView()
    {
        // Scroll offset is the reason this cannot be cached: the same mark answers differently on
        // the next frame, which is why the anchor-hint-changed path re-derives it on scroll.
        var s = new Session(rows: 6).Prompt();
        s.Write(string.Concat(Enumerable.Repeat("out\r\n", 20)));
        int scrollback = s.Buffer.Scrollback.Count;

        Assert.False(s.TryResolve(out _));
        Assert.True(s.TryResolve(out int row, scrollOffset: scrollback));
        Assert.Equal(0, row);
    }

    [Fact]
    public void MarkBelowTheRenderedViewport_HasNoAnchor()
    {
        // The renderer can be drawing fewer rows than the buffer holds for a frame during a
        // resize. "On screen" is a question about what is drawn, so the resolver is asked with the
        // renderer's row count rather than the buffer's.
        var s = new Session(rows: 6).Write("a\r\nb\r\nc\r\nd\r\n").Prompt();

        Assert.True(s.TryResolve(out int row));
        Assert.Equal(4, row);
        Assert.False(s.TryResolve(out _, visibleRows: 3));
    }

    [Fact]
    public void ClearedScrollback_BumpsTheGenerationAndTheMarkIsRefused()
    {
        // CSI 3J zeroes both row counters, so a stale AbsoluteRow resolves to a plausible in-range
        // row holding unrelated content. Only the generation epoch catches it.
        var s = new Session(rows: 6).Write("a\r\nb\r\nc\r\n").Prompt();
        Assert.True(s.TryResolve(out _));

        s.Write("\x1b[3J");

        Assert.NotEqual(s.Mark.Generation, s.Buffer.Scrollback.Generation);
        Assert.False(s.TryResolve(out _));
    }

    [Fact]
    public void MarkEvictedFromHistory_HasNoAnchor()
    {
        var s = new Session(cols: 40, rows: 3, maxHistory: 128).Prompt();
        for (int i = 0; i < 200; i++)
        {
            s.Write($"line {i}\r\n");
        }

        Assert.True(s.Buffer.Scrollback.TotalRowsEvicted > 0, "the budget must actually evict");
        Assert.True(s.Mark.AbsoluteRow - s.Buffer.Scrollback.TotalRowsEvicted < 0);
        Assert.False(s.TryResolve(out _, scrollOffset: s.Buffer.Scrollback.Count));
    }

    [Fact]
    public void AltScreenMark_HasNoAnchor()
    {
        var s = new Session(rows: 6);
        s.Write("\x1b[?1049h").Prompt();

        Assert.True(s.Mark.IsAltScreen);
        Assert.False(s.TryResolve(out _));
    }

    [Fact]
    public void MarkTakenOnTheMainScreenWhileTheAltScreenIsActive_HasNoAnchor()
    {
        var s = new Session(rows: 6).Prompt();
        Assert.True(s.TryResolve(out _));

        s.Write("\x1b[?1049h");

        Assert.True(s.Buffer.IsAltScreenActive);
        Assert.False(s.TryResolve(out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveVisibleRows_HaveNoAnchor(int visibleRows)
    {
        var s = new Session(rows: 6).Prompt();

        Assert.False(s.TryResolve(out _, visibleRows: visibleRows));
    }

    [Fact]
    public void MarkMidViewportAtAPartialScrollOffset_ResolvesToTheShiftedRow()
    {
        // The row-0 scroll case above pins that scrollOffset is *used*; it cannot tell the sign
        // apart from its own negation, because the answer there is 0 either way. Here the mark is
        // three rows below the top of a partially scrolled viewport, so the expected row is 3 with
        // the offset applied in the right direction and -3 (refused) with it inverted.
        var s = new Session(rows: 6).Write("a\r\nb\r\nc\r\n").Prompt();
        s.Write(string.Concat(Enumerable.Repeat("out\r\n", 20)));
        int scrollback = s.Buffer.Scrollback.Count;
        Assert.True(scrollback > 3, "the mark must be scrollable back to a mid-viewport row");

        // Scrolled all the way back the marked row is 3 (three lines of output preceded the prompt);
        // easing off by one row moves it up to 2, and so on. Any of those is only correct if
        // viewportTop = Scrollback.Count - scrollOffset.
        Assert.True(s.TryResolve(out int fullyScrolled, scrollOffset: scrollback));
        Assert.Equal(3, fullyScrolled);

        Assert.True(s.TryResolve(out int oneRowLess, scrollOffset: scrollback - 1));
        Assert.Equal(2, oneRowLess);

        Assert.True(s.TryResolve(out int twoRowsLess, scrollOffset: scrollback - 2));
        Assert.Equal(1, twoRowsLess);

        // And it leaves the viewport again on the way back to the live edge.
        Assert.False(s.TryResolve(out _, scrollOffset: scrollback - 4));
    }

    [Fact]
    public void NegativeScrollOffset_HasNoAnchor()
    {
        var s = new Session(rows: 6).Prompt();

        Assert.False(s.TryResolve(out _, scrollOffset: -1));
    }

    [Fact]
    public void ScrollOffsetPastTheEndOfHistory_HasNoAnchor()
    {
        // Nobody can scroll further back than the history that exists, and the arithmetic does not
        // fail loudly if they claim to: an over-large offset drives viewportTop negative, which
        // shifts every row down by the overshoot and hands back a plausible in-range row for a
        // mark that is nowhere near it. Refusing is the only honest answer, same as every other
        // ambiguity here.
        var s = new Session(rows: 6).Prompt();
        s.Write(string.Concat(Enumerable.Repeat("out\r\n", 20)));
        int scrollback = s.Buffer.Scrollback.Count;

        Assert.True(s.TryResolve(out int row, scrollOffset: scrollback), "the largest legal offset still resolves");
        Assert.Equal(0, row);
        Assert.False(s.TryResolve(out _, scrollOffset: scrollback + 1));
        Assert.False(s.TryResolve(out _, scrollOffset: scrollback + 5));
    }

    [Fact]
    public void ScrollOffsetPastTheEndOfHistoryOnAnUnscrolledBuffer_HasNoAnchor()
    {
        // The degenerate shape of the same bug: with no scrollback at all, offset 1 would put
        // viewportTop at -1 and report the first-row mark as row 1.
        var s = new Session(rows: 6).Prompt();

        Assert.Equal(0, s.Buffer.Scrollback.Count);
        Assert.True(s.TryResolve(out int row));
        Assert.Equal(0, row);
        Assert.False(s.TryResolve(out _, scrollOffset: 1));
    }

    [Fact]
    public void NullBuffer_HasNoAnchor()
    {
        var mark = new ShellIntegrationMark(Row: 0, Column: 0, AbsoluteRow: 0, IsAltScreen: false, Generation: 0);

        Assert.False(ShellMarkAnchorResolver.TryResolveVisualRow(null!, mark, 0, 6, out _));
    }

    [Fact]
    public void MarkPastTheEndOfTheBuffer_HasNoAnchor()
    {
        var buffer = new TerminalBuffer(40, 6);
        var mark = new ShellIntegrationMark(
            Row: 0, Column: 0, AbsoluteRow: 500, IsAltScreen: false,
            Generation: buffer.Scrollback.Generation);

        Assert.False(ShellMarkAnchorResolver.TryResolveVisualRow(buffer, mark, 0, 6, out _));
    }
}
