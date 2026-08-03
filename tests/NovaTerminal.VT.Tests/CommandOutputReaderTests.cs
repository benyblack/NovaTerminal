using System;
using System.Linq;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// The output-region reader: what a finished command printed, taken off the grid between the
/// <c>OSC 133;C</c> and <c>OSC 133;D</c> marks.
/// </summary>
/// <remarks>
/// <para>
/// V2 Phase 4a task 1. Fix mode's recognisers are only as good as this text, and the failure mode
/// that matters is not "captured nothing" - it is "captured the wrong rows and matched a pattern
/// against someone else's output". Most of what is pinned below is therefore refusal: a dead
/// coordinate generation, an alt screen, a region that has not been written to.
/// </para>
/// <para>
/// Everything is driven through the real parser and write path, like
/// <see cref="GridQueryReaderTests"/>, because wrap flags, scrollback paging and wide-cell
/// continuations are produced there and nowhere else.
/// </para>
/// </remarks>
public class CommandOutputReaderTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";

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

        /// <summary>True when the C edge produced a usable output-region start.</summary>
        public bool HasOutputStart => _outputStart.HasValue;

        public ShellIntegrationMark OutputStart => _outputStart!.Value;

        public Session Write(string data)
        {
            Parser.Process(data);
            return this;
        }

        /// <summary>A prompt as every bootstrap paints it: A, the prompt text, then B.</summary>
        public Session Prompt(string text = "$ ") => Write(PromptStart + text + PromptEnd);

        /// <summary>The user's line, then the C mark the shell emits when it accepts it.</summary>
        public Session Submit(string commandLine)
        {
            Write(commandLine);
            Write("\x1b]133;C\x07");
            _outputStart = CommandOutputReader.TryCaptureOutputStart(Buffer, _commandLineMark, out var start)
                ? start
                : null;
            return this;
        }

        /// <summary>Command output, preceded by the newline the shell echoes after Enter.</summary>
        public Session Output(params string[] lines)
            => Write("\r\n" + string.Join("\r\n", lines) + "\r\n");

        /// <summary>Raw output with no leading newline, for the byte-exact cases.</summary>
        public Session RawOutput(string data) => Write(data);

        public bool TryReadTail(out string tail)
        {
            tail = string.Empty;
            return _outputStart is ShellIntegrationMark start
                && CommandOutputReader.TryReadOutputTail(Buffer, start, out tail);
        }

        public string ReadTail()
        {
            Assert.True(TryReadTail(out string tail), "the output region should have been readable");
            return tail;
        }
    }

    // ---------------------------------------------------------------- simple reads

    [Fact]
    public void ASingleOutputLine_IsTheWholeTail()
    {
        var s = new Session()
            .Prompt()
            .Submit("git status")
            .Output("fatal: not a git repository");

        Assert.Equal("fatal: not a git repository", s.ReadTail());
    }

    [Fact]
    public void SeveralOutputLines_AreJoinedWithNewlines()
    {
        var s = new Session()
            .Prompt()
            .Submit("npm run build")
            .Output("npm error Missing script: \"build\"", "npm error", "npm error   npm run");

        Assert.Equal(
            "npm error Missing script: \"build\"\nnpm error\nnpm error   npm run",
            s.ReadTail());
    }

    /// <summary>
    /// The input line is excluded. It is the row the C mark sits on, and including it would put the
    /// command text inside the text the recognisers scan - so <c>git status</c> failing outside a
    /// repository would look like output containing the word "status".
    /// </summary>
    [Fact]
    public void TheCommandLineItselfIsNotPartOfTheOutput()
    {
        var s = new Session()
            .Prompt()
            .Submit("cat missing.txt")
            .Output("cat: missing.txt: No such file or directory");

        Assert.DoesNotContain("cat missing.txt", s.ReadTail(), StringComparison.Ordinal);
    }

    /// <summary>The prompt is not either, and it is on the same row as the command.</summary>
    [Fact]
    public void ThePromptIsNotPartOfTheOutput()
    {
        var s = new Session()
            .Prompt("user@host:~/work$ ")
            .Submit("false")
            .Output("nothing useful");

        Assert.Equal("nothing useful", s.ReadTail());
    }

    [Fact]
    public void ACommandThatPrintedNothing_ReadsAsEmptyRatherThanFailing()
    {
        var s = new Session()
            .Prompt()
            .Submit("false")
            .Write("\r\n");

        Assert.True(s.TryReadTail(out string tail));
        Assert.Equal(string.Empty, tail);
    }

    /// <summary>
    /// At <c>D</c> the shell has emitted its final newline and the cursor is parked on a row the
    /// next prompt has not painted yet. That row is slack, not a trailing empty line of output.
    /// </summary>
    [Fact]
    public void TrailingBlankRowsAreDropped()
    {
        var s = new Session()
            .Prompt()
            .Submit("build")
            .Output("error: build failed")
            .Write("\r\n\r\n");

        Assert.Equal("error: build failed", s.ReadTail());
    }

    [Fact]
    public void TrailingSpacesOnARowAreDropped()
    {
        var s = new Session()
            .Prompt()
            .Submit("build")
            .Output("error: build failed          ");

        Assert.Equal("error: build failed", s.ReadTail());
    }

    // ---------------------------------------------------------------- wrapping

    /// <summary>
    /// The property the recognisers depend on. PowerShell's not-recognised message is 150-odd
    /// characters and wraps on any realistic pane; if the reader emitted a <c>'\n'</c> at the wrap
    /// point, a pattern spanning it would match on a wide pane and fail on a narrow one - which is
    /// the worst kind of bug to find in the field.
    /// </summary>
    [Fact]
    public void ASoftWrappedOutputLineIsOneLogicalLine()
    {
        var s = new Session(cols: 20)
            .Prompt()
            .Submit("gti")
            .Output("The term 'gti' is not recognized as a name of a cmdlet.");

        Assert.Equal("The term 'gti' is not recognized as a name of a cmdlet.", s.ReadTail());
        Assert.DoesNotContain('\n', s.ReadTail());
    }

    [Fact]
    public void HardBreaksBetweenWrappedLinesAreKept()
    {
        var s = new Session(cols: 20)
            .Prompt()
            .Submit("thing")
            .Output(
                "first line long enough to wrap around",
                "second line long enough to wrap too");

        string tail = s.ReadTail();
        Assert.Equal(
            "first line long enough to wrap around\nsecond line long enough to wrap too",
            tail);
    }

    /// <summary>
    /// A command line that wrapped over three rows: the output starts after the <em>last</em> of
    /// them, not after the row the cursor happened to be on when C arrived.
    /// </summary>
    [Fact]
    public void AWrappedCommandLineDoesNotLeakIntoTheOutput()
    {
        string longCommand = "echo " + new string('x', 60);
        var s = new Session(cols: 20)
            .Prompt()
            .Submit(longCommand)
            .Output("boom");

        string tail = s.ReadTail();
        Assert.Equal("boom", tail);
        Assert.DoesNotContain("xxxx", tail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public void OnlyTheLastFortyLogicalLinesAreKept()
    {
        string[] lines = Enumerable.Range(1, 60).Select(i => $"line {i}").ToArray();
        var s = new Session()
            .Prompt()
            .Submit("noisy")
            .Output(lines);

        string[] tail = s.ReadTail().Split('\n');

        Assert.Equal(CommandOutputReader.MaxOutputLines, tail.Length);
        Assert.Equal("line 21", tail[0]);
        Assert.Equal("line 60", tail[^1]);
    }

    [Fact]
    public void TheCharacterCapBoundsTheResult()
    {
        // 40 lines of 300 characters is 12 KB, well past the 8 KB ceiling, and the line budget
        // alone would not stop it.
        string[] lines = Enumerable.Range(1, 40).Select(i => $"{i:D3} " + new string('z', 296)).ToArray();
        var s = new Session(cols: 300)
            .Prompt()
            .Submit("verbose")
            .Output(lines);

        string tail = s.ReadTail();

        Assert.True(tail.Length <= CommandOutputReader.MaxOutputChars, $"tail was {tail.Length} chars");
        Assert.EndsWith("zzz", tail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- staleness

    /// <summary>
    /// The case no arithmetic can catch. <c>CSI 3J</c> - what <c>clear(1)</c> sends with the
    /// <c>E3</c> capability, i.e. routinely - zeroes both the eviction counter and the row numbers,
    /// so the pre-clear absolute row resolves to a perfectly plausible row holding somebody else's
    /// content. Only the generation epoch distinguishes them.
    /// </summary>
    [Fact]
    public void AfterAScrollbackReset_TheRegionIsRefusedRatherThanMisread()
    {
        var s = new Session()
            .Prompt()
            .Submit("something")
            .Output("real output");

        s.Write("\x1b[3J\x1b[H");
        s.Write("unrelated content that is now on the row the mark points at");

        Assert.False(s.TryReadTail(out _));
    }

    [Fact]
    public void WhileTheAltScreenIsActive_TheRegionIsRefused()
    {
        var s = new Session()
            .Prompt()
            .Submit("vim notes.txt")
            .Output("some output");

        s.Write("\x1b[?1049h");

        Assert.False(s.TryReadTail(out _));
    }

    [Fact]
    public void ACommandAcceptedOnTheAltScreen_HasNoOutputRegion()
    {
        var s = new Session();
        s.Write("\x1b[?1049h");
        s.Prompt().Submit("inner");

        Assert.False(s.HasOutputStart);
    }

    /// <summary>
    /// Output that pushed the C edge out of scrollback. The request was for the <em>last</em> forty
    /// lines and those are still there, so the start clamps to the oldest surviving row rather than
    /// refusing: a shorter answer, never a wrong one.
    /// </summary>
    [Fact]
    public void WhenTheRegionStartHasBeenEvicted_TheSurvivingTailIsStillReturned()
    {
        // The byte budget is derived from MaxHistory and eviction works in whole 64-row pages, so
        // 128 is the smallest budget that both retains history and actually evicts.
        string[] lines = Enumerable.Range(1, 400).Select(i => $"line {i}").ToArray();
        var s = new Session(maxHistory: 128)
            .Prompt()
            .Submit("very noisy")
            .Output(lines);

        Assert.True(s.TryReadTail(out string tail));

        string[] rows = tail.Split('\n');
        Assert.Equal("line 400", rows[^1]);
        Assert.DoesNotContain(rows, row => row.Contains("very noisy", StringComparison.Ordinal));
    }

    /// <summary>
    /// A bare <c>133;C</c> with no preceding <c>B</c> - legal FinalTerm, and what several
    /// third-party remote snippets emit. The cursor row is the fallback, and it is right whenever
    /// the input line did not wrap.
    /// </summary>
    [Fact]
    public void WithoutAPromptMark_TheCursorRowBoundsTheRegion()
    {
        var s = new Session();
        s.Write("$ git status");
        s.Submit(string.Empty);
        s.Output("fatal: not a git repository");

        Assert.True(s.HasOutputStart);
        Assert.Equal("fatal: not a git repository", s.ReadTail());
    }

    // ---------------------------------------------------------------- wide cells

    [Fact]
    public void WideCharactersCountOnceInTheOutput()
    {
        var s = new Session()
            .Prompt()
            .Submit("show")
            .Output("你好 world");

        Assert.Equal("你好 world", s.ReadTail());
    }
}
