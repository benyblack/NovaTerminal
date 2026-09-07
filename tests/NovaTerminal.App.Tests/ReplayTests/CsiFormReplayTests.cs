using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NovaTerminal.Replay;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.ReplayTests;

/// <summary>
/// Replay coverage for #274's CSI identity rules: a sequence is named by its final byte together
/// with its leader and intermediates, and one that cannot be read in full is discarded.
/// </summary>
/// <remarks>
/// <para>
/// The direct parser tests in <c>AnsiParserHardeningTests</c> hand each sequence to
/// <c>AnsiParser.Process</c> whole. Going through the replay path adds the thing they cannot
/// reach: chunk boundaries. The parser's CSI state - the accumulated parameter buffer and the
/// truncation flag that decides whether the final byte dispatches at all - lives across
/// <c>Process</c> calls, so a sequence split between chunks exercises different code than the
/// same sequence delivered in one piece.
/// </para>
/// <para>
/// Required by CONTRIBUTING.md: VT semantics and buffer state changes carry replay tests.
/// </para>
/// </remarks>
public sealed class CsiFormReplayTests
{
    /// <summary>
    /// The live case. DA1 advertises sixel, so sixel-capable clients probe XTSMGRAPHICS; before
    /// #274 each probe scrolled the screen, so a session that probed mid-output lost lines off
    /// the top with nothing in the recording to explain it.
    /// </summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_XtSmGraphicsProbeBetweenOutput_DoesNotScroll()
    {
        var lines = await ReplayAsync(
            "one\r\n",
            "two\r\n",
            "\x1b[?1;1;0S",   // XTSMGRAPHICS - not SU
            "three\r\n",
            "\x1b[?2;1;0S",
            "four");

        Assert.Equal("one", lines[0]);
        Assert.Equal("two", lines[1]);
        Assert.Equal("three", lines[2]);
        Assert.Equal("four", lines[3]);
    }

    /// <summary>
    /// The leader arrives in one chunk and the final byte in the next. The parser must still see
    /// the sequence as qualified: a chunk boundary is not a sequence boundary.
    /// </summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_QualifiedSequenceSplitAcrossChunks_IsStillQualified()
    {
        var lines = await ReplayAsync(
            "one\r\n",
            "two\r\n",
            "\x1b[?1;1",  // chunk ends mid-parameters
            ";0S",        // ... and the final byte lands here
            "three");

        Assert.Equal("one", lines[0]);
        Assert.Equal("two", lines[1]);
        Assert.Equal("three", lines[2]);
    }

    /// <summary>
    /// Truncation is decided while collecting bytes and acted on when the final byte arrives, so
    /// the flag has to survive the chunk boundary between the two. If it did not, the overlong
    /// sequence would dispatch on its truncated prefix and move the cursor.
    /// </summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_TruncatedSequenceSplitAcrossChunks_IsDiscarded()
    {
        var lines = await ReplayAsync(
            "top\r\n",
            "\x1b[5;1H",
            "\x1b[" + new string('1', 70000),  // overruns the cap, final byte not yet seen
            "A",                                // would be CUU if the prefix were dispatched
            "here");

        // CUU would have moved the cursor off row 5 and written "here" somewhere above it.
        Assert.Equal("top", lines[0]);
        Assert.Equal("here", lines[4]);
    }

    /// <summary>The ordinary forms still work through the same path, split or not.</summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_BareSequencesStillApply()
    {
        var lines = await ReplayAsync(
            "alpha\r\nbravo\r\ncharlie",
            "\x1b[2;1H",
            "\x1b[",      // CSI alone at a chunk boundary
            "K",          // EL - erase "bravo"
            "BRAVO");

        Assert.Equal("alpha", lines[0]);
        Assert.Equal("BRAVO", lines[1]);
        Assert.Equal("charlie", lines[2]);
    }

    /// <summary>
    /// Cursor reports through the replay path: the private DECXCPR form, and the DECOM-relative
    /// coordinates both forms now use.
    /// </summary>
    /// <remarks>
    /// A report is the one place the parser writes back to the stream, so a recording that
    /// contains a query is only faithful if the reply is too. The DECOM conversion is what makes
    /// this worth replaying rather than only unit-testing: the scroll region, the mode and the
    /// cursor move are three separate sequences, so the reply depends on state accumulated across
    /// the recording rather than on the query alone.
    /// </remarks>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_CursorReports_UnderOriginMode_AreRegionRelative()
    {
        var responses = await ReplayResponsesAsync(
            "\x1b[5;20r",  // scroll region rows 5..20
            "\x1b[?6h",    // DECOM on
            "\x1b[2;3H",   // row 2 OF THE REGION
            "\x1b[6n",     // CPR
            "\x1b[?6n");   // DECXCPR

        Assert.Equal(new[] { "\x1b[2;3R", "\x1b[?2;3R" }, responses);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_CursorReports_WithoutOriginMode_AreAbsolute()
    {
        var responses = await ReplayResponsesAsync(
            "\x1b[5;20r",
            "\x1b[2;3H",
            "\x1b[6n",
            "\x1b[?6n");

        Assert.Equal(new[] { "\x1b[2;3R", "\x1b[?2;3R" }, responses);
    }

    /// <summary>
    /// The private query split across a chunk boundary still answers in the private form: the
    /// leader arrives in one chunk and the final byte in the next.
    /// </summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Replay_DecXcprSplitAcrossChunks_StillAnswersPrivately()
    {
        var responses = await ReplayResponsesAsync(
            "\x1b[3;7H",
            "\x1b[?",
            "6n");

        Assert.Equal(new[] { "\x1b[?3;7R" }, responses);
    }

    private static async Task<string[]> ReplayResponsesAsync(params string[] chunks)
    {
        string recPath = Path.Combine(Path.GetTempPath(), $"nova-csi-cpr-{Path.GetRandomFileName()}.rec");

        try
        {
            using (var recorder = new PtyRecorder(recPath, 20, 8))
            {
                foreach (string chunk in chunks)
                {
                    byte[] data = Encoding.UTF8.GetBytes(chunk);
                    recorder.RecordChunk(data, data.Length);
                }
            }

            var buffer = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(buffer);
            var responses = new List<string>();
            parser.OnResponse = responses.Add;

            var runner = new ReplayRunner(recPath);
            await runner.RunAsync(async data =>
            {
                parser.Process(Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            return responses.ToArray();
        }
        finally
        {
            if (File.Exists(recPath))
            {
                File.Delete(recPath);
            }
        }
    }

    private static async Task<string[]> ReplayAsync(params string[] chunks)
    {
        string recPath = Path.Combine(Path.GetTempPath(), $"nova-csi-form-{Path.GetRandomFileName()}.rec");

        try
        {
            using (var recorder = new PtyRecorder(recPath, 20, 8))
            {
                foreach (string chunk in chunks)
                {
                    byte[] data = Encoding.UTF8.GetBytes(chunk);
                    recorder.RecordChunk(data, data.Length);
                }
            }

            var buffer = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async data =>
            {
                parser.Process(Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            return BufferSnapshot.Capture(buffer).Lines.ToArray();
        }
        finally
        {
            if (File.Exists(recPath))
            {
                File.Delete(recPath);
            }
        }
    }
}
