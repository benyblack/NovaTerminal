using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// OSC 133;B (prompt end / start of user input) and the buffer position captured with it.
/// The position is the anchor Command Assist V2 uses to read the live command line straight
/// out of the grid instead of mirroring keystrokes, so "where exactly did the mark land" is
/// the contract under test here, not merely "did the callback fire".
/// </summary>
public class Osc133CommandStartMarkTests
{
    private const string PromptEnd = "\x1b]133;B\x07";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<ShellIntegrationMark> Marks) Make(
        int cols = 40,
        int rows = 5)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer);
        var marks = new List<ShellIntegrationMark>();
        parser.OnCommandStarted = mark => marks.Add(mark);
        return (parser, buffer, marks);
    }

    [Fact]
    public void Mark_IsReportedAtTheCursorCellFollowingThePrompt()
    {
        var (parser, _, marks) = Make();

        // The shape every bootstrap produces: A before the prompt text, B at its tail.
        parser.Process("\x1b]133;A\x07");
        parser.Process("user@host:~$ ");
        parser.Process(PromptEnd);

        var mark = Assert.Single(marks);
        Assert.Equal(0, mark.Row);
        Assert.Equal("user@host:~$ ".Length, mark.Column);
        Assert.False(mark.IsAltScreen);
    }

    [Fact]
    public void Mark_TracksTheRowAsOutputPushesThePromptDown()
    {
        var (parser, _, marks) = Make();

        parser.Process("line one\r\nline two\r\n");
        parser.Process("$ ");
        parser.Process(PromptEnd);

        var mark = Assert.Single(marks);
        Assert.Equal(2, mark.Row);
        Assert.Equal(2, mark.Column);
        Assert.Equal(2L, mark.AbsoluteRow);
    }

    [Fact]
    public void AbsoluteRow_KeepsCountingWhileRowsScrollIntoScrollback()
    {
        // Row is relative to the buffer's current addressing space (scrollback + viewport),
        // so it keeps growing as long as nothing is evicted; AbsoluteRow adds the eviction
        // count on top and is the identity a consumer stores.
        var (parser, buffer, marks) = Make(cols: 40, rows: 3);

        for (int i = 0; i < 6; i++)
        {
            parser.Process($"line {i}\r\n");
            parser.Process("$ ");
            parser.Process(PromptEnd);
            parser.Process("\r");
        }

        Assert.Equal(6, marks.Count);
        Assert.True(buffer.Scrollback.Count > 0, "rows should have moved into scrollback");

        // Strictly increasing, one row per prompt, and never behind the scrollback floor.
        for (int i = 1; i < marks.Count; i++)
        {
            Assert.True(marks[i].AbsoluteRow > marks[i - 1].AbsoluteRow);
        }

        // The last mark still resolves to a live row: current row =
        // AbsoluteRow - TotalRowsEvicted, which is what a later consumer computes.
        long evicted = buffer.Scrollback.TotalRowsEvicted;
        Assert.Equal(marks[^1].Row, (int)(marks[^1].AbsoluteRow - evicted));
        Assert.InRange(marks[^1].Row, 0, buffer.TotalLines - 1);
    }

    [Fact]
    public void Mark_ReportsAltScreenCapture()
    {
        var (parser, _, marks) = Make();

        parser.Process("\x1b[?1049h"); // enter alt screen
        parser.Process("\x1b[H");      // home the cursor, as a full-screen app would
        parser.Process("> ");
        parser.Process(PromptEnd);

        var mark = Assert.Single(marks);
        Assert.True(mark.IsAltScreen);
        // No scrollback on the alt screen: the row is the viewport row and the absolute id
        // shares no numbering with main-screen marks.
        Assert.Equal(0, mark.Row);
        Assert.Equal(0L, mark.AbsoluteRow);
    }

    [Fact]
    public void Mark_IsEmittedOncePerPromptRedraw()
    {
        // The mark rides in PS1/PROMPT/fish_prompt, so a prompt repaint re-emits it with
        // fresh coordinates. That is the property that lets a consumer recover after a
        // resize, so a second B without an intervening C/D must not be swallowed.
        var (parser, _, marks) = Make();

        parser.Process("\x1b]133;A\x07$ " + PromptEnd);
        parser.Process("\r\x1b]133;A\x07$ " + PromptEnd);

        Assert.Equal(2, marks.Count);
    }

    [Fact]
    public void Mark_IsReportedEvenWithoutAPrecedingPromptStart()
    {
        // B before any A (mid-session attach, or an integration that only emits B).
        var (parser, _, marks) = Make();

        parser.Process("$ " + PromptEnd);

        var mark = Assert.Single(marks);
        Assert.Equal(2, mark.Column);
    }

    [Theory]
    [InlineData("\x1b]133;B;\x07")]                  // trailing separator, empty attribute
    [InlineData("\x1b]133;B;aid=7\x07")]             // FinalTerm-style key=value attribute
    [InlineData("\x1b]133;B;;;\x07")]                // repeated empty attributes
    [InlineData("\x1b]133;B\x1b\\")]                 // ST-terminated instead of BEL
    public void MalformedOrDecoratedPayloads_StillProduceTheMark(string sequence)
    {
        var (parser, _, marks) = Make();

        parser.Process("$ ");
        parser.Process(sequence);

        var mark = Assert.Single(marks);
        Assert.Equal(2, mark.Column);
    }

    [Theory]
    [InlineData("\x1b]133;\x07")]      // no marker letter
    [InlineData("\x1b]133;b\x07")]     // lowercase: markers are case-sensitive
    [InlineData("\x1b]133;BB\x07")]    // not the B marker
    [InlineData("\x1b]133\x07")]       // no payload at all
    public void NonBPayloads_DoNotProduceTheMark(string sequence)
    {
        var (parser, _, marks) = Make();

        parser.Process(sequence);

        Assert.Empty(marks);
    }

    [Fact]
    public void MarkSurvivesSplitProcessCalls()
    {
        // PTY reads chop escape sequences at arbitrary offsets; the mark must be reported
        // once, from the position the cursor holds when the sequence completes.
        var (parser, _, marks) = Make();

        parser.Process("$ \x1b]133");
        parser.Process(";B");
        parser.Process("\x07");

        var mark = Assert.Single(marks);
        Assert.Equal(2, mark.Column);
        Assert.Equal(0, mark.Row);
    }

    [Fact]
    public void MarkDoesNotDisturbTheBufferContents()
    {
        // The mark is zero-width: nothing is written, the cursor does not move.
        var (parser, buffer, _) = Make();

        parser.Process("$ ");
        int colBefore = buffer.CursorCol;
        parser.Process(PromptEnd);

        Assert.Equal(colBefore, buffer.CursorCol);
        parser.Process("ls");
        Assert.Equal("$ ls", RowText(buffer, 0).TrimEnd());
    }

    private static string RowText(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var chars = new char[buffer.Cols];
            for (int col = 0; col < buffer.Cols; col++)
            {
                chars[col] = buffer.GetCellAbsolute(col, row).Character;
            }
            return new string(chars).Replace('\0', ' ');
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
