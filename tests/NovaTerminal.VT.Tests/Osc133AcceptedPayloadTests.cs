using System;
using System.Text;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// What <c>OSC 133;C</c> means when the payload is not a base64 command (V2 Phase 2b).
/// </summary>
/// <remarks>
/// <para>
/// Until Phase 2b the parser raised <c>OnCommandAccepted</c> only for a decodable base64 payload,
/// which was safe precisely because the only sessions with a listener attached were ones Nova had
/// instrumented itself, and all four Nova bootstraps send <c>133;C;&lt;base64&gt;</c>. Phase 2b arms
/// the same listener for SSH sessions instrumented by whatever the user installed, and FinalTerm
/// does not require a payload at all: iTerm2's and VS Code's snippets send a bare <c>133;C</c>, and
/// some hand-rolled ones send plain text.
/// </para>
/// <para>
/// <c>C</c> is the edge that closes Command Assist's command-input window. A swallowed <c>C</c>
/// leaves the grid reader treating a running command's output as a command line until <c>D</c>
/// arrives, so the event has to fire for every <c>C</c> and carry <see langword="null"/> when there
/// is no text to carry.
/// </para>
/// <para>
/// The other half is what must <em>not</em> become command text. Base64 has no self-describing
/// shape - <c>make</c>, <c>date</c> and <c>true</c> all decode - and FinalTerm allows
/// <c>key=value</c> attributes on these marks, so "it decoded" and "it is printable" are each
/// insufficient on their own. Every case that cannot be answered has to answer
/// <see langword="null"/>, because a null costs one history entry and a wrong answer puts a string
/// the user never typed into permanent history.
/// </para>
/// </remarks>
public class Osc133AcceptedPayloadTests
{
    private static (AnsiParser Parser, List<string?> Accepted, List<bool> Fired) Make()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var accepted = new List<string?>();
        var fired = new List<bool>();
        parser.OnCommandAccepted = text =>
        {
            accepted.Add(text);
            fired.Add(true);
        };
        return (parser, accepted, fired);
    }

    private static List<string?> Accept(string osc)
    {
        (AnsiParser parser, List<string?> accepted, _) = Make();
        parser.Process(osc);
        return accepted;
    }

    // ---- the payload we ship ---------------------------------------------------------------

    [Fact]
    public void Base64Payload_IsDecoded()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("git status --short"));

        Assert.Equal("git status --short", Assert.Single(Accept($"\x1b]133;C;{encoded}\x07")));
    }

    /// <summary>
    /// The reason base64 is the payload Nova's own bootstraps use: a command containing the
    /// parameter separator survives it, and nothing else would.
    /// </summary>
    [Fact]
    public void Base64Payload_SurvivesSemicolonsAndNewlines()
    {
        const string command = "for i in 1 2 3; do\n    echo \"$i\"\ndone";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        Assert.Equal(command, Assert.Single(Accept($"\x1b]133;C;{encoded}\x07")));
    }

    // ---- no payload -------------------------------------------------------------------------

    /// <summary>
    /// The shape iTerm2's and VS Code's shell integrations emit. The event must still fire: it is
    /// the lifecycle edge, and dropping it was the Phase 2b landmine.
    /// </summary>
    [Theory]
    [InlineData("\x1b]133;C\x07")]
    [InlineData("\x1b]133;C;\x07")]
    [InlineData("\x1b]133;C;   \x07")]
    public void PayloadlessC_FiresWithNullText(string osc)
    {
        List<string?> accepted = Accept(osc);

        Assert.Single(accepted);
        Assert.Null(accepted[0]);
    }

    // ---- plain-text payload -------------------------------------------------------------------

    /// <summary>
    /// Some third-party integrations send the command text unencoded. Passed through as written -
    /// lossy for a command containing <c>;</c>, since the parameter split has already happened, but
    /// strictly better than discarding it.
    /// </summary>
    [Theory]
    [InlineData("git status", "git status")]
    [InlineData("ls -la /tmp", "ls -la /tmp")]
    [InlineData("./build.sh --release", "./build.sh --release")]
    public void PlainTextPayload_IsPassedThrough(string payload, string expected)
    {
        Assert.Equal(expected, Assert.Single(Accept($"\x1b]133;C;{payload}\x07")));
    }

    /// <summary>
    /// The trap in the plain-text reading. <c>aid=7</c> is printable, so a bare "printable means
    /// command text" rule would write it into the user's permanent history. FinalTerm allows
    /// <c>key=value</c> attributes on these marks, so an identifier followed by <c>=</c> that did
    /// not already decode as base64 is an attribute, not a command.
    /// </summary>
    [Theory]
    [InlineData("aid=7")]
    [InlineData("cl=m")]
    [InlineData("_private_id=abc")]
    [InlineData("some-key=value")]
    public void FinalTermAttributePayload_IsNotTreatedAsCommandText(string payload)
    {
        Assert.Null(Assert.Single(Accept($"\x1b]133;C;{payload}\x07")));
    }

    /// <summary>
    /// The attribute rule must not eat a real command that happens to contain <c>=</c>. Only a
    /// payload whose <em>entire</em> prefix up to the first <c>=</c> is an identifier qualifies.
    /// </summary>
    [Theory]
    [InlineData("FOO=bar make", "FOO=bar make")]
    [InlineData("echo a=b", "echo a=b")]
    [InlineData("git diff HEAD~1", "git diff HEAD~1")]
    public void PlainTextPayloadContainingEquals_IsStillCommandText(string payload, string expected)
    {
        Assert.Equal(expected, Assert.Single(Accept($"\x1b]133;C;{payload}\x07")));
    }

    // ---- garbage ------------------------------------------------------------------------------

    /// <summary>
    /// A padded payload that is valid base64 by shape but decodes to bytes that are not UTF-8. The
    /// decode "succeeds" and produces U+FFFD replacement characters, which is the tell; without the
    /// plausibility check those would be written to history as a command.
    /// </summary>
    /// <remarks>
    /// Padding is why this can answer null rather than falling through to the plain-text reading:
    /// it is the one piece of self-description base64 has, so <c>//79/A==</c> is definitively a
    /// broken blob rather than something a user typed. See
    /// <see cref="UnpaddedBase64ShapedGarbage_FallsThroughToThePlainTextReading"/> for the case
    /// where that signal is absent.
    /// </remarks>
    [Fact]
    public void PaddedBase64PayloadDecodingToNonUtf8Bytes_IsNotAccepted()
    {
        string encoded = Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB });
        Assert.EndsWith("=", encoded, StringComparison.Ordinal);

        Assert.Null(Assert.Single(Accept($"\x1b]133;C;{encoded}\x07")));
    }

    /// <summary>
    /// The documented residual, pinned so it stays a decision rather than a surprise. Without
    /// padding there is no way to tell a four-character blob from a four-character command:
    /// <c>date</c> and <c>AQID</c> are the same shape. The fall-through returns the payload as
    /// written, which is wrong for a blob and right for <c>date</c> - and only one of those two
    /// exists in the wild.
    /// </summary>
    [Fact]
    public void UnpaddedBase64ShapedGarbage_FallsThroughToThePlainTextReading()
    {
        string encoded = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03 });
        Assert.DoesNotContain("=", encoded, StringComparison.Ordinal);

        string? result = Assert.Single(Accept($"\x1b]133;C;{encoded}\x07"));

        // Returned as literal text, never as the decoded garbage.
        Assert.Equal(encoded, result);
        Assert.False(result!.Contains((char)0xFFFD), "decoded garbage must never be surfaced");
    }

    /// <summary>
    /// The other half of the same rule, and why base64 cannot be trusted on shape alone: a short
    /// plain-text command whose characters happen to be a valid base64 quad. <c>date</c> decodes to
    /// three non-UTF-8 bytes; the plausibility check rejects the decode and the plain-text reading
    /// then answers correctly.
    /// </summary>
    [Theory]
    [InlineData("date")]
    [InlineData("make")]
    [InlineData("true")]
    public void ShortCommandThatLooksLikeBase64_IsReadAsPlainText(string command)
    {
        Assert.Equal(command, Assert.Single(Accept($"\x1b]133;C;{command}\x07")));
    }

    /// <summary>
    /// A padded payload that decodes to text with embedded control characters is not something a
    /// user typed at a prompt.
    /// </summary>
    [Fact]
    public void PaddedPayloadDecodingToControlCharacters_IsNotAccepted()
    {
        string encoded = Convert.ToBase64String(new byte[] { 0x01, 0x02 });
        Assert.EndsWith("=", encoded, StringComparison.Ordinal);

        Assert.Null(Assert.Single(Accept($"\x1b]133;C;{encoded}\x07")));
    }

    /// <summary>
    /// A base64 payload that decodes to a genuinely short command is accepted through the base64
    /// path, not the plain-text one - the negative control for
    /// <see cref="ShortCommandThatLooksLikeBase64_IsReadAsPlainText"/>.
    /// </summary>
    [Fact]
    public void Base64PayloadDecodingToAShortCommand_IsDecoded()
    {
        // "bHM=" is base64 for "ls". Also the case the FinalTerm-attribute check must never see,
        // since base64 padding ends in '=' and would match the attribute shape.
        Assert.Equal("ls", Assert.Single(Accept("\x1b]133;C;bHM=\x07")));
    }

    /// <summary>
    /// Extra FinalTerm parameters after the payload are ignored rather than making the mark
    /// undecodable.
    /// </summary>
    [Fact]
    public void ExtraParametersAfterThePayload_AreIgnored()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("git status"));

        Assert.Equal("git status", Assert.Single(Accept($"\x1b]133;C;{encoded};aid=7\x07")));
    }
}
