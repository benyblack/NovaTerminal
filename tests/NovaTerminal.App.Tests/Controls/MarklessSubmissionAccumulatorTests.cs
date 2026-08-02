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
    [InlineData(Key.Back, KeyModifiers.Control)]
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
