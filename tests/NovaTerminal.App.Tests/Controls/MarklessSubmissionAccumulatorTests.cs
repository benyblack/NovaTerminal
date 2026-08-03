using Avalonia.Input;
using NovaTerminal.Controls;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// The accumulator's own rules, away from the pane. <c>PaneMarklessCaptureTests</c> covers the
/// wiring; this covers the edges that are hard to reach through a terminal.
/// </summary>
public class MarklessSubmissionAccumulatorTests
{
    // ------------------------------------------------ device replies (dogfood report 2, cmd.exe)

    /// <summary>
    /// A device reply stands history capture down for the rest of the line. It is not observable
    /// which of the two things happened - the program that issued the query read its answer, or the
    /// answer landed in the shell's line editor as literal input to the left of everything typed
    /// since - and the second one would put a command the user never ran into permanent history.
    /// </summary>
    [Fact]
    public void AfterADeviceReply_CaptureRefusesEvenThoughTheTypedTextIsKnown()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.AppendTypedText("git status");
        accumulator.ObserveDeviceReply();

        Assert.Null(accumulator.TryReadSubmission());
    }

    /// <summary>
    /// <strong>And it deliberately does not answer the insertion question, which is the fix for the
    /// owner's "Enter puts nothing in the terminal on cmd.exe" report.</strong>
    /// </summary>
    /// <remarks>
    /// ConPTY and Clink both probe the terminal while the first prompt is drawn, so a local markless
    /// pane receives one of these before the user has touched the keyboard. Treating it as a poison
    /// made <c>IsCleanAndEmpty</c> false from session start, which is the sole gate on degraded-mode
    /// insertion: <c>Ctrl+R</c> then <c>Enter</c> on a brand-new <c>cmd.exe</c> pane sent nothing,
    /// silently, until the user had submitted a command by hand. Insertion's failure mode is a
    /// visible, editable line rather than a permanent record, so it gets the other answer.
    /// </remarks>
    [Fact]
    public void AfterADeviceReplyOnAnUntouchedLine_TheLineIsStillProvablyEmpty()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.ObserveDeviceReply();

        Assert.True(accumulator.IsCleanAndEmpty);
        Assert.False(accumulator.IsPoisoned);
    }

    /// <summary>
    /// The carve-out is scoped to "nothing has been typed". A device reply plus typed characters is
    /// still a non-empty line, so insertion refuses for the ordinary reason.
    /// </summary>
    [Fact]
    public void AfterADeviceReplyAndTyping_TheLineIsNotEmpty()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.ObserveDeviceReply();
        accumulator.AppendTypedText("gi");

        Assert.False(accumulator.IsCleanAndEmpty);
    }

    /// <summary>A real poison still wins over an empty buffer, device reply or not.</summary>
    [Fact]
    public void ADeviceReplyDoesNotUnPoison()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.Poison();
        accumulator.ObserveDeviceReply();

        Assert.False(accumulator.IsCleanAndEmpty);
        Assert.Null(accumulator.TryReadSubmission());
    }

    /// <summary>
    /// The reply is scoped to the command line it arrived on, like every other fact here: a new line
    /// starts clean and can be captured again.
    /// </summary>
    [Fact]
    public void Reset_ClearsTheDeviceReply()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.ObserveDeviceReply();
        accumulator.Reset();
        accumulator.AppendTypedText("git status");

        Assert.Equal("git status", accumulator.TryReadSubmission());
    }

    [Fact]
    public void TypingThenReading_ReturnsExactlyWhatWasTyped()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.AppendTypedText("git");
        accumulator.AppendTypedText(" ");
        accumulator.AppendTypedText("status");

        Assert.Equal("git status", accumulator.TryReadSubmission());
    }

    /// <summary>
    /// Backspace at an empty prompt does nothing in every shell, so it must not poison: otherwise
    /// one stray keypress before the user starts typing costs the whole command.
    /// </summary>
    [Fact]
    public void BackspaceOnAnEmptyBuffer_IsANoOpRatherThanAPoison()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.ObserveBackspace();
        accumulator.AppendTypedText("ls");

        Assert.Equal("ls", accumulator.TryReadSubmission());
    }

    /// <summary>
    /// "The last character" is not a well-defined thing to remove when the buffer ends in a
    /// surrogate pair or a combining sequence: what the shell's line editor deletes and what one
    /// UTF-16 unit is are not reliably the same. Refuse rather than guess.
    /// </summary>
    [Theory]
    // An astral-plane emoji (two UTF-16 units), then "e" plus a combining acute (two code points
    // that render as one character).
    [InlineData("cd \U0001F600")]
    [InlineData("echo é")]
    public void BackspaceOverAnAmbiguousCharacter_Poisons(string typed)
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.AppendTypedText(typed);
        accumulator.ObserveBackspace();

        Assert.True(accumulator.IsPoisoned);
        Assert.Null(accumulator.TryReadSubmission());
    }

    /// <summary>
    /// Text input carrying a newline submits without going through the Enter path that reads and
    /// resets, so the accumulator would otherwise carry the line into the next command.
    /// </summary>
    [Fact]
    public void TextInputContainingANewline_Poisons()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.AppendTypedText("echo one\necho two");

        Assert.Null(accumulator.TryReadSubmission());
    }

    [Fact]
    public void BeyondTheLengthCap_Poisons()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.AppendTypedText(new string('x', MarklessSubmissionAccumulator.MaxTrackedLength));
        Assert.NotNull(accumulator.TryReadSubmission());

        accumulator.AppendTypedText("x");
        Assert.Null(accumulator.TryReadSubmission());
    }

    [Fact]
    public void Reset_ClearsBothTheTextAndThePoison()
    {
        var accumulator = new MarklessSubmissionAccumulator();

        accumulator.AppendTypedText("git sta");
        accumulator.Poison();
        accumulator.Reset();
        accumulator.AppendTypedText("ls");

        Assert.Equal("ls", accumulator.TryReadSubmission());
    }

    /// <summary>
    /// The classification is an allow-list. A key nobody thought about poisons, because an
    /// allow-list that is missing a harmless key costs a capture and a deny-list that is missing a
    /// line-editing key writes a command the user never ran into permanent history.
    /// </summary>
    [Theory]
    [InlineData(Key.MediaNextTrack, KeyModifiers.None)]
    [InlineData(Key.BrowserBack, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Control)]
    [InlineData(Key.A, KeyModifiers.Alt)]
    [InlineData(Key.A, KeyModifiers.Meta)]
    [InlineData(Key.Enter, KeyModifiers.Alt)]
    [InlineData(Key.Back, KeyModifiers.Alt)]
    public void UnknownAndChordedKeys_Poison(Key key, KeyModifiers modifiers)
    {
        Assert.Equal(
            AccumulatorKeyEffect.Poison,
            MarklessSubmissionAccumulator.ClassifyKey(key, modifiers, wasHandledByAssist: false));
    }

    /// <summary>
    /// `Enter` and `Backspace` are modeled only completely unmodified, and the reason is the kitty
    /// keyboard protocol rather than the legacy encoder. With the disambiguate tier active,
    /// `TerminalView` encodes a modified Enter or Backspace as CSI u — `Ctrl+Backspace` becomes
    /// `CSI 127;5u`, `Shift+Enter` becomes `CSI 13;2u` — and takes the early return, so
    /// `BackspaceObserved` / `EnterObserved` never fire. A kitty-aware line editor deletes a whole
    /// word on that sequence while the accumulator, having been told nothing, keeps every
    /// character. The modifier is invisible by the time the observation events arrive, so the
    /// refusal has to happen here.
    /// </summary>
    [Theory]
    [InlineData(Key.Back, KeyModifiers.Control)]
    [InlineData(Key.Back, KeyModifiers.Shift)]
    [InlineData(Key.Enter, KeyModifiers.Control)]
    [InlineData(Key.Enter, KeyModifiers.Shift)]
    public void ModifiedEnterAndBackspace_Poison(Key key, KeyModifiers modifiers)
    {
        Assert.Equal(
            AccumulatorKeyEffect.Poison,
            MarklessSubmissionAccumulator.ClassifyKey(key, modifiers, wasHandledByAssist: false));
    }

    /// <summary>
    /// The <c>AltGr</c> carve-out. Windows Avalonia reports <c>AltGr</c> as <c>Control|Alt</c>, so
    /// without this a German, French, Nordic, Turkish or Polish user poisons the line on every
    /// <c>@</c>, <c>{</c>, <c>[</c>, <c>\</c>, <c>|</c> and <c>~</c> — which is to say, captures
    /// nothing.
    /// </summary>
    /// <remarks>
    /// It stays fail-closed because <c>TerminalView</c> sends nothing to the PTY for
    /// <c>Ctrl+Alt</c> plus a text-producing key: <c>EncodeKittyKey</c> returns null on the
    /// <c>Control|Alt</c> pair, <c>EncodeAltKey</c> returns null for it, and the legacy <c>Ctrl</c>
    /// branch requires <c>!Alt</c>. The only path to the shell is the composed <c>WM_CHAR</c>,
    /// which arrives as <c>OnTextInput</c> and is appended — so the accumulator sees every change
    /// this keypress makes to the line.
    /// </remarks>
    [Theory]
    [InlineData(Key.Q)]        // AltGr+Q -> '@' on a German layout
    [InlineData(Key.D2)]       // AltGr+2 -> '@' on many layouts
    [InlineData(Key.Oem5)]     // AltGr+<key> -> '\' or '|' depending on layout
    [InlineData(Key.Oem102)]
    [InlineData(Key.NumPad7)]
    public void AltGrPlusATextProducingKey_LeavesTheBufferAlone(Key key)
    {
        Assert.Equal(
            AccumulatorKeyEffect.None,
            MarklessSubmissionAccumulator.ClassifyKey(
                key, KeyModifiers.Control | KeyModifiers.Alt, wasHandledByAssist: false));
    }

    /// <summary>
    /// The other half of the carve-out: <c>Ctrl+Alt</c> plus a key that does <em>not</em> produce
    /// text still poisons, because those <em>do</em> reach the shell as control bytes and the
    /// accumulator never learns what the line editor did with them.
    /// </summary>
    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Back)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Left)]
    [InlineData(Key.Delete)]
    public void AltGrPlusANonTextKey_StillPoisons(Key key)
    {
        Assert.Equal(
            AccumulatorKeyEffect.Poison,
            MarklessSubmissionAccumulator.ClassifyKey(
                key, KeyModifiers.Control | KeyModifiers.Alt, wasHandledByAssist: false));
    }

    /// <summary>
    /// The carve-out is for <c>AltGr</c>, not for "any pile of modifiers". Adding <c>Meta</c> makes
    /// it an application chord again on every platform that has one.
    /// </summary>
    [Fact]
    public void CtrlAltMetaPlusATextProducingKey_Poisons()
    {
        Assert.Equal(
            AccumulatorKeyEffect.Poison,
            MarklessSubmissionAccumulator.ClassifyKey(
                Key.Q,
                KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta,
                wasHandledByAssist: false));
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.D4, KeyModifiers.Shift)]
    [InlineData(Key.Space, KeyModifiers.None)]
    [InlineData(Key.OemMinus, KeyModifiers.None)]
    [InlineData(Key.NumPad7, KeyModifiers.None)]
    [InlineData(Key.Divide, KeyModifiers.None)]
    [InlineData(Key.Enter, KeyModifiers.None)]
    [InlineData(Key.Back, KeyModifiers.None)]
    [InlineData(Key.LeftShift, KeyModifiers.None)]
    public void ModeledAndPrintableKeys_LeaveTheBufferAlone(Key key, KeyModifiers modifiers)
    {
        Assert.Equal(
            AccumulatorKeyEffect.None,
            MarklessSubmissionAccumulator.ClassifyKey(key, modifiers, wasHandledByAssist: false));
    }

    /// <summary>
    /// A key Command Assist consumed never reached <c>TerminalView</c>'s encoder, so it never
    /// reached the shell and the command line is untouched. (`Ctrl+Enter` is the exception and the
    /// pane poisons at that call site, because accepting a suggestion sends text.)
    /// </summary>
    [Fact]
    public void AKeyCommandAssistConsumed_DoesNotPoison()
    {
        Assert.Equal(
            AccumulatorKeyEffect.None,
            MarklessSubmissionAccumulator.ClassifyKey(Key.Up, KeyModifiers.None, wasHandledByAssist: true));
    }

    [Fact]
    public void CtrlC_Resets()
    {
        Assert.Equal(
            AccumulatorKeyEffect.Reset,
            MarklessSubmissionAccumulator.ClassifyKey(Key.C, KeyModifiers.Control, wasHandledByAssist: false));
    }
}
