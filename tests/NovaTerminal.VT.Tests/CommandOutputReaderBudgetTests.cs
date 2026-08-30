using System;
using System.Linq;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// The budgeted and markless reads: <see cref="CommandOutputReader.TryReadOutputTail(TerminalBuffer, ShellIntegrationMark, in OutputTailBudget, out string)"/>
/// and <see cref="CommandOutputReader.TryReadRecentTail(TerminalBuffer, out string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The default caps (40 logical lines / 8 KB) are sized for error recognition. The Agent Output
/// panel reads a whole agent response through a larger budget, and sessions without shell
/// integration have no <c>OSC 133;C</c> mark to bound the region at all - both paths share the
/// marked read's walk, so the tests pin what matters about each: the budget is a hard cap applied
/// before the string leaves the reader, the newest lines win, and the staleness rules (generation
/// epoch, alt screen) hold unchanged.
/// </para>
/// <para>
/// Driven through the real parser and write path, like <see cref="CommandOutputReaderTests"/>,
/// because wrap flags and scrollback paging are produced there and nowhere else.
/// </para>
/// </remarks>
public class CommandOutputReaderBudgetTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

    private static string[] Lines(int count)
        => Enumerable.Range(1, count).Select(i => $"line {i}").ToArray();

    private sealed class Session
    {
        private ShellIntegrationMark? _commandLineMark;
        private ShellIntegrationMark? _outputStart;

        public Session(int cols = 40, int rows = 8, int? maxHistory = null)
        {
            Buffer = new TerminalBuffer(cols, rows);
            if (maxHistory is int history)
            {
                Buffer.MaxHistory = history;
            }

            Parser = new AnsiParser(Buffer);
            Parser.OnCommandStarted = mark => _commandLineMark = mark;
        }

        public TerminalBuffer Buffer { get; }

        public AnsiParser Parser { get; }

        public ShellIntegrationMark? OutputStart => _outputStart;

        public Session Write(string data)
        {
            Parser.Process(data);
            return this;
        }

        public Session Prompt(string text = "$ ") => Write(PromptStart + text + PromptEnd);

        public Session Submit(string commandLine)
        {
            Write(commandLine);
            Write("\x1b]133;C\x07");
            _outputStart = CommandOutputReader.TryCaptureOutputStart(Buffer, _commandLineMark, out var start)
                ? start
                : null;
            return this;
        }

        public Session Output(params string[] lines)
            => Write("\r\n" + string.Join("\r\n", lines) + "\r\n");
    }

    // ---------------------------------------------------------------- budgeted marked read

    [Fact]
    public void TheDefaultBudget_ReadsExactlyWhatTheLegacyOverloadReads()
    {
        var s = new Session(maxHistory: 256)
            .Prompt()
            .Submit("noisy")
            .Output(Lines(60));

        Assert.True(CommandOutputReader.TryReadOutputTail(s.Buffer, s.OutputStart!.Value, out string legacy));
        Assert.True(CommandOutputReader.TryReadOutputTail(
            s.Buffer, s.OutputStart.Value, OutputTailBudget.Default, out string budgeted));

        Assert.Equal(legacy, budgeted);
        Assert.Equal(40, legacy.Split('\n').Length);
    }

    [Fact]
    public void ALargerLineBudget_ReadsBeyondTheDefaultCap()
    {
        var s = new Session(maxHistory: 256)
            .Prompt()
            .Submit("noisy")
            .Output(Lines(60));

        var budget = new OutputTailBudget(MaxLines: 100, MaxChars: 64 * 1024, MaxRows: 512);
        Assert.True(CommandOutputReader.TryReadOutputTail(s.Buffer, s.OutputStart!.Value, budget, out string tail));

        string[] lines = tail.Split('\n');
        Assert.Equal(60, lines.Length);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal("line 60", lines[^1]);
    }

    [Fact]
    public void ALineBudget_ClampsToTheRequestedLines_KeepingTheNewest()
    {
        var s = new Session(maxHistory: 256)
            .Prompt()
            .Submit("noisy")
            .Output(Lines(100));

        var budget = new OutputTailBudget(MaxLines: 10, MaxChars: 64 * 1024, MaxRows: 512);
        Assert.True(CommandOutputReader.TryReadOutputTail(s.Buffer, s.OutputStart!.Value, budget, out string tail));

        string[] lines = tail.Split('\n');
        Assert.Equal(10, lines.Length);
        Assert.Equal("line 91", lines[0]);
        Assert.Equal("line 100", lines[^1]);
    }

    [Fact]
    public void ACharBudget_ClampsHard_AndKeepsTheEnd()
    {
        var s = new Session()
            .Prompt()
            .Submit("dense")
            .Output("aaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbb", "zzz");

        var budget = new OutputTailBudget(MaxLines: 100, MaxChars: 10, MaxRows: 512);
        Assert.True(CommandOutputReader.TryReadOutputTail(s.Buffer, s.OutputStart!.Value, budget, out string tail));

        Assert.True(tail.Length <= 10, $"tail was {tail.Length} chars");
        Assert.EndsWith("zzz", tail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The budget changes the caps, not the staleness rules: a generation mismatch is still fatal,
    /// because a larger budget reading someone else's rows would be worse than a smaller one.
    /// </summary>
    [Fact]
    public void ABudgetedRead_AfterAScrollbackReset_IsStillRefused()
    {
        var s = new Session()
            .Prompt()
            .Submit("something")
            .Output("real output");

        s.Write("\x1b[3J\x1b[H");
        s.Write("unrelated content that is now on the row the mark points at");

        var budget = new OutputTailBudget(MaxLines: 100, MaxChars: 64 * 1024, MaxRows: 512);
        Assert.False(CommandOutputReader.TryReadOutputTail(s.Buffer, s.OutputStart!.Value, budget, out _));
    }

    // ---------------------------------------------------------------- markless recent tail

    [Fact]
    public void RecentTail_WithoutAMark_ReturnsTheRowsAboveTheCursor_PromptIncluded()
    {
        // No Submit, no C capture - the markless shape. The honest answer is everything still on
        // the grid above the cursor, prompt line included.
        var s = new Session()
            .Prompt()
            .Output("first line", "second line");

        Assert.Null(s.OutputStart);
        Assert.True(CommandOutputReader.TryReadRecentTail(s.Buffer, out string tail));

        // The prompt row's trailing space is dropped by the reader's trailing-blank trim.
        Assert.Equal("$\nfirst line\nsecond line", tail);
    }

    [Fact]
    public void RecentTail_RespectsTheBudget_KeepingTheNewest()
    {
        var s = new Session(maxHistory: 256)
            .Prompt()
            .Output(Lines(50));

        var budget = new OutputTailBudget(MaxLines: 5, MaxChars: 64 * 1024, MaxRows: 512);
        Assert.True(CommandOutputReader.TryReadRecentTail(s.Buffer, budget, out string tail));

        string[] lines = tail.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal("line 46", lines[0]);
        Assert.Equal("line 50", lines[^1]);
    }

    [Fact]
    public void RecentTail_WhileTheAltScreenIsActive_IsRefused()
    {
        var s = new Session()
            .Prompt()
            .Output("some output");

        s.Write("\x1b[?1049h");

        Assert.False(CommandOutputReader.TryReadRecentTail(s.Buffer, out _));
    }

    [Fact]
    public void RecentTail_JoinsSoftWrappedRowsLikeTheMarkedRead()
    {
        // One logical line wider than the pane (40 cols), wrapped by the terminal. The join is
        // what keeps a wrapped paragraph parseable as one markdown line - a contiguous match of
        // the full 90-char string proves the wrapped halves were rejoined.
        var longLine = new string('x', 90);
        var s = new Session()
            .Prompt()
            .Output(longLine);

        Assert.True(CommandOutputReader.TryReadRecentTail(s.Buffer, out string tail));
        Assert.Contains(longLine, tail, StringComparison.Ordinal);
    }
}
