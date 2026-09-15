using System.Collections.Generic;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// Shell-integration marks re-anchored across a reflowing (width-changing) resize.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug these pin.</b> A width change rebuilds the scrollback store, which bumps
/// <c>ScrollbackPages.Generation</c>, and every reader refuses a mark whose epoch no longer
/// matches. That refusal is correct — a pre-reflow <c>AbsoluteRow</c> resolves to a plausible
/// but wrong row — but it left the session markless, because <b>a resize does not make the
/// shell re-emit <c>OSC 133;B</c></b>: PSReadLine (measured on 2.3) repaints the input line
/// without re-running the prompt function, and zsh/fish behave the same unless the user wired a
/// resize hook. So Command Assist lost the passive bubble, the grid-truth query and the
/// structured capture for the whole of that command line, and only recovered when the user
/// submitted something and got a fresh prompt. Since the first thing a user does with a new
/// window is size it, that presented as "the first prompt of a session is dead".
/// </para>
/// <para>
/// <b>What the fix does, and does not, change.</b> The generation check is untouched: a mark
/// carrying a dead epoch is still refused, and <c>GridQueryReaderTests</c> keeps a test that
/// says so for a caller holding its own pre-reflow copy. What changed is that the buffer now
/// carries the live marks (<see cref="TerminalBuffer.CommandStartMark"/>,
/// <see cref="TerminalBuffer.CommandOutputStartMark"/>) and the reflow re-anchors them by
/// logical-line index plus offset-in-logical-line — the same mapping it already applies to the
/// cursor, the saved cursors and inline images — so the mark comes out the other side with new
/// coordinates <i>and</i> the new generation. There is no stale epoch left to reject.
/// </para>
/// <para>
/// Everything is driven through the real parser and the real <c>Resize</c>, because the wrap
/// flags and the scrollback/viewport split the re-anchoring depends on are produced by that path
/// and nowhere else. The <see cref="Session"/> helper mirrors what <c>TerminalPane</c> does with
/// the parser callback: store the newest <c>B</c> on the buffer.
/// </para>
/// </remarks>
public class ShellMarkReflowTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

    private sealed class Session
    {
        public Session(int cols = 40, int rows = 6, int? maxHistory = null)
        {
            Buffer = new TerminalBuffer(cols, rows);
            if (maxHistory is int history)
            {
                Buffer.MaxHistory = history;
            }

            Parser = new AnsiParser(Buffer);

            // Exactly what TerminalPane does on OSC 133;B: publish the newest mark to the
            // buffer, which is what makes it survive a reflow. The raw marks are kept too so a
            // test can assert on the pre-reflow copy.
            Parser.OnCommandStarted = mark =>
            {
                RawMarks.Add(mark);
                Buffer.CommandStartMark = mark;
            };
        }

        public TerminalBuffer Buffer { get; }

        public AnsiParser Parser { get; }

        public List<ShellIntegrationMark> RawMarks { get; } = new();

        /// <summary>The mark as it was captured, never re-anchored.</summary>
        public ShellIntegrationMark RawMark => RawMarks[^1];

        /// <summary>The live mark the buffer carries — re-anchored by any reflow since capture.</summary>
        public ShellIntegrationMark? TrackedMark => Buffer.CommandStartMark;

        public Session Write(string data)
        {
            Parser.Process(data);
            return this;
        }

        public Session Prompt(string text = "$ ") => Write(PromptStart + text + PromptEnd);

        public bool TryReadTracked(out GridCommandLine line)
        {
            line = default;
            return TrackedMark is ShellIntegrationMark live
                && GridQueryReader.TryReadCommandLine(Buffer, live, out line);
        }

        public GridCommandLine ReadTracked()
        {
            Assert.True(TryReadTracked(out GridCommandLine line), "the tracked mark should still resolve");
            return line;
        }
    }

    // ------------------------------------------------------------------ the regression

    [Fact]
    public void NarrowingResize_ReanchorsTheMark_SoTheCommandLineStillReads()
    {
        // The live repro, reduced: prompt, two typed characters, then the resize the user makes
        // as soon as the window is up. Before the fix the tracked mark's generation went stale
        // here and the pane was markless until the next prompt.
        var s = new Session(cols: 40, rows: 6).Prompt().Write("ec");

        long generationBefore = s.Buffer.Scrollback.Generation;
        s.Buffer.Resize(24, 6);

        Assert.NotEqual(generationBefore, s.Buffer.Scrollback.Generation);
        Assert.Equal(s.Buffer.Scrollback.Generation, s.TrackedMark!.Value.Generation);

        GridCommandLine line = s.ReadTracked();
        Assert.Equal("ec", line.Text);
        Assert.Equal(2, line.CursorOffset);
    }

    [Fact]
    public void WideningResize_ReanchorsTheMark()
    {
        var s = new Session(cols: 40, rows: 6).Prompt().Write("git status");

        s.Buffer.Resize(80, 6);

        Assert.Equal("git status", s.ReadTracked().Text);
    }

    [Fact]
    public void APreReflowCopyOfTheMarkIsStillRefused()
    {
        // The generation contract, unchanged and deliberately so. The fix does not loosen the
        // check; it removes the stale epoch by re-anchoring the buffer's own copy. A caller that
        // squirrelled the mark away before the resize still holds coordinates for a layout that
        // no longer exists, and still gets nothing.
        var s = new Session(cols: 40, rows: 6).Prompt().Write("git status");

        s.Buffer.Resize(24, 6);

        Assert.NotEqual(s.Buffer.Scrollback.Generation, s.RawMark.Generation);
        Assert.False(GridQueryReader.TryReadCommandLine(s.Buffer, s.RawMark, out _));
    }

    [Fact]
    public void ReanchoringSurvivesScrollbackAboveThePrompt()
    {
        // The interesting half of the arithmetic: rows above the prompt are re-wrapped too, so
        // the mark's row moves by however many extra physical rows the narrower width produced.
        var s = new Session(cols: 40, rows: 6);
        for (int i = 0; i < 12; i++) s.Write($"a rather long output line number {i}\r\n");
        s.Prompt().Write("ls -la");

        Assert.True(s.Buffer.Scrollback.Count > 0, "there must be history to re-wrap");
        int markRowBefore = s.TrackedMark!.Value.Row;

        s.Buffer.Resize(20, 6);

        Assert.NotEqual(markRowBefore, s.TrackedMark!.Value.Row);
        Assert.Equal("ls -la", s.ReadTracked().Text);
    }

    [Fact]
    public void ReanchoringSurvivesAnInputLineThatRewrapsAcrossRows()
    {
        // Narrow enough that the prompt plus the typed text no longer fits on one row, so the
        // mark's own logical line is split. The re-anchor has to land on the right physical row
        // *and* the right column within it.
        var s = new Session(cols: 60, rows: 6).Prompt().Write("echo one two three four five");

        s.Buffer.Resize(20, 6);

        GridCommandLine line = s.ReadTracked();
        Assert.Equal("echo one two three four five", line.Text);
        Assert.Equal(28, line.CursorOffset);
        Assert.True(line.EndRow > line.StartRow, "the input must actually have re-wrapped");
    }

    [Fact]
    public void RepeatedResizes_KeepTheMarkUsable()
    {
        // A drag produces a burst of them; nothing may accumulate drift.
        var s = new Session(cols: 40, rows: 6).Prompt().Write("git status");

        foreach (int cols in new[] { 30, 55, 22, 80, 31 })
        {
            s.Buffer.Resize(cols, 6);
            Assert.Equal("git status", s.ReadTracked().Text);
        }
    }

    [Fact]
    public void HeightOnlyResize_DoesNotDisturbTheTrackedMark()
    {
        // Negative control: the fast path never reflowed and never bumped the generation, so the
        // re-anchoring must not touch anything here either.
        var s = new Session(cols: 40, rows: 6);
        for (int i = 0; i < 4; i++) s.Write($"line {i}\r\n");
        s.Prompt().Write("git status");

        ShellIntegrationMark before = s.TrackedMark!.Value;
        s.Buffer.Resize(40, 3);

        Assert.Equal(before, s.TrackedMark!.Value);
        Assert.Equal("git status", s.ReadTracked().Text);
    }

    // ------------------------------------------------------------------ still refused

    [Fact]
    public void AClearedScrollbackStillKillsTheTrackedMark()
    {
        // CSI 3J genuinely destroys the content the mark named; re-anchoring is neither possible
        // nor wanted. The tracked mark keeps its old epoch and the reader refuses it, which is
        // the pre-fix behaviour and the correct one.
        var s = new Session(cols: 40, rows: 10);
        for (int i = 0; i < 5; i++) s.Write($"out {i}\r\n");
        s.Prompt().Write("ls");

        Assert.True(s.TryReadTracked(out _));

        s.Write("\x1b[3J");

        Assert.NotEqual(s.Buffer.Scrollback.Generation, s.TrackedMark!.Value.Generation);
        Assert.False(s.TryReadTracked(out _));
    }

    [Fact]
    public void AnAltScreenMarkIsNotReanchored()
    {
        // The alt screen has no scrollback and shares no row numbering with the main screen the
        // reflow rebuilds. Mapping such a mark into main-screen coordinates would invent one.
        var s = new Session(cols: 40, rows: 6);
        s.Write("\x1b[?1049h").Write("\x1b[H").Prompt("> ").Write("q");

        Assert.True(s.TrackedMark!.Value.IsAltScreen);
        s.Buffer.Resize(24, 6);

        Assert.False(s.TryReadTracked(out _));
    }

    [Fact]
    public void AMarkWhoseRowNoLongerExistsIsCleared_NotGuessedAt()
    {
        // A mark pointing past the end of the reconstructed content cannot be placed. The slot
        // must come back empty rather than holding coordinates the reflow made up.
        var buffer = new TerminalBuffer(40, 6)
        {
            CommandStartMark = new ShellIntegrationMark(
                Row: 0, Column: 0, AbsoluteRow: 5_000, IsAltScreen: false, Generation: 0),
        };

        buffer.Resize(24, 6);

        Assert.Null(buffer.CommandStartMark);
    }

    // ------------------------------------------------------------------ the other mark

    [Fact]
    public void TheOutputRegionMarkIsReanchoredToo()
    {
        // Captured at OSC 133;C and read at OSC 133;D. A resize in between is exactly as likely
        // as one on the prompt - it is the window in which a long command is running - and
        // losing it costs Fix mode the failing command's output.
        var s = new Session(cols: 40, rows: 10).Prompt().Write("false");

        Assert.True(CommandOutputReader.TryCaptureOutputStart(
            s.Buffer, s.TrackedMark, out ShellIntegrationMark outputStart));
        s.Buffer.CommandOutputStartMark = outputStart;

        s.Write("\r\nboom: it went wrong\r\n");
        s.Buffer.Resize(28, 10);

        Assert.NotNull(s.Buffer.CommandOutputStartMark);
        Assert.Equal(s.Buffer.Scrollback.Generation, s.Buffer.CommandOutputStartMark!.Value.Generation);
        Assert.True(CommandOutputReader.TryReadOutputTail(
            s.Buffer, s.Buffer.CommandOutputStartMark!.Value, out string tail));
        Assert.Contains("boom: it went wrong", tail);
    }

    // ------------------------------------------------------------------ overlay anchoring

    [Fact]
    public void TheOverlayAnchorResolvesAgainstTheReanchoredMark()
    {
        // ShellMarkAnchorResolver enforces the same generation rule, so the bubble was placed by
        // the geometric fallback (or not at all) for the whole of a resized command line.
        var s = new Session(cols: 40, rows: 6);
        for (int i = 0; i < 3; i++) s.Write($"line {i}\r\n");
        s.Prompt().Write("ls");

        s.Buffer.Resize(26, 6);

        Assert.True(ShellMarkAnchorResolver.TryResolveVisualRow(
            s.Buffer, s.TrackedMark!.Value, scrollOffset: 0, visibleRows: 6, out int visualRow));
        Assert.InRange(visualRow, 0, 5);
    }
}
