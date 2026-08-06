using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The planner's contract in two halves: the suffix arithmetic (unchanged since V1) and the
/// refusals (new in Phase 1c, and the reason the arithmetic can now be trusted). Every refusal here
/// is a line V1 would have happily computed a delta against, using a keystroke mirror that had no
/// idea the line had moved.
/// </summary>
public sealed class CommandAssistInsertionPlannerTests
{
    private static AssistQuerySnapshot Line(
        string text,
        int? cursorOffset = null,
        bool isMultiline = false,
        bool rightPromptTrimmed = false)
    {
        return new AssistQuerySnapshot(text, cursorOffset ?? text.Length, isMultiline, rightPromptTrimmed);
    }

    // ---------------------------------------- the typed prefix projection (PR #293, blocker 1)

    /// <summary>
    /// <c>TextBeforeCursor</c> is the query projection every prefix consumer wants: the painted line up
    /// to the cursor, so PSReadLine's inline prediction - real cells past the cursor - is left out.
    /// </summary>
    [Theory]
    [InlineData("echo hello-world", 2, "ec")]
    [InlineData("echo hello-world", 1, "e")]
    [InlineData("git status", 10, "git status")]
    [InlineData("git status", 4, "git ")]
    [InlineData("", 0, "")]
    public void TextBeforeCursor_IsTheLineUpToTheCursor(string text, int cursorOffset, string expected)
    {
        Assert.Equal(expected, Line(text, cursorOffset).TextBeforeCursor);
    }

    /// <summary>
    /// Clamped rather than trusting the invariant. <c>CursorOffset</c> is documented as always valid, and
    /// two comparisons remove a whole class of crash from a future reader change.
    /// </summary>
    [Theory]
    [InlineData("git status", -1, "")]
    [InlineData("git status", 99, "git status")]
    public void TextBeforeCursor_ClampsAnOutOfRangeCursor(string text, int cursorOffset, string expected)
    {
        Assert.Equal(expected, Line(text, cursorOffset).TextBeforeCursor);
    }

    /// <summary>
    /// And it is not a substitute for <c>IsUsableAsTypedPrefix</c>: a prediction and a mid-line cursor are
    /// indistinguishable on the grid, so insertion still refuses on both. Having a good prefix to rank on
    /// does not make an append safe.
    /// </summary>
    [Fact]
    public void TextBeforeCursor_DoesNotMakeAMidLineCursorInsertable()
    {
        AssistQuerySnapshot line = Line("echo hello-world", cursorOffset: 2);

        Assert.Equal("ec", line.TextBeforeCursor);
        Assert.False(line.IsUsableAsTypedPrefix);
        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(line, "echo hello", out string? textToSend));
        Assert.Null(textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenLineIsPrefix_ReturnsOnlySuffix()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st"),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("atus", textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenLineMatchesExactly_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git status"),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// An empty line is a fact the grid reported, not a missing one, so the whole command is safe
    /// to send. The distinction between this and the markless case below is the entire point of
    /// modelling the query as a nullable snapshot rather than a string.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_WhenTheLineIsObservedEmpty_ReturnsTheFullSuggestion()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(string.Empty),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("git status", textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenSuggestionDoesNotStartWithTheLine_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("kubectl"),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenSelectedCommandIsEmpty_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st"),
            selectedCommand: string.Empty,
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// Refusal 1 - no grid truth. A markless session cannot see the command line, so it cannot know
    /// that sending "git status" onto a line already reading "git s" produces "git sgit status".
    /// Insertion is a prefix-dependent feature and degraded mode does not offer those.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_WithoutGridTruth_Refuses()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            query: null,
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// Refusal 2 - the cursor is not at the end. The user pressed Home or Left; sent text lands at
    /// the cursor, so the "suffix" would be spliced into the middle of the command.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void TryCreateInsertion_WhenTheCursorIsNotAtTheEnd_Refuses(int cursorOffset)
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st", cursorOffset),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// Refusal 3 - multiline. The snapshot contains whatever the shell painted as a continuation
    /// prompt, so the text is not a prefix the user typed even when it happens to start one.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_WhenTheEntryIsMultiline_Refuses()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("for i in 1 2 3\n> do echo", isMultiline: true),
            selectedCommand: "for i in 1 2 3\n> do echo $i; done",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// <strong>A trimmed right prompt is no longer a refusal, and this is the owner's "Enter puts
    /// nothing in the terminal on Windows PowerShell" report at planner scope.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be refusal 4, on the reasoning that the tail of the line was the reader's inference
    /// and the tail is what a suffix append attaches to. The first half is true and the second is not:
    /// <c>GridQueryReader.FindRightPromptGapStart</c> floors its search at the cursor column, so the
    /// trim boundary is always at or after the cursor and never removes anything the append is measured
    /// against. What survives is <see cref="AssistQuerySnapshot.IsUsableAsTypedPrefix"/>'s cursor test,
    /// which is the real guard.
    /// </para>
    /// <para>
    /// Left in as a refusal it made Command Assist inert for anyone with a right-aligned prompt -
    /// oh-my-posh, zsh <c>RPROMPT</c>, starship - because the flag is then set on every prompt the
    /// shell paints.
    /// </para>
    /// </remarks>
    [Fact]
    public void TryCreateInsertion_WhenARightPromptWasTrimmed_StillSendsTheSuffix()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st", rightPromptTrimmed: true),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("atus", textToSend);
    }

    /// <summary>
    /// The empty-line case of the same prompt, which is the one the owner actually pressed
    /// <c>Enter</c> on: <c>Ctrl+R</c> at a bare prompt whose row carries a right-aligned badge.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_OnAnEmptyLineWithARightPromptTrimmed_SendsTheWholeCommand()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(string.Empty, rightPromptTrimmed: true),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("git status", textToSend);
    }

    /// <summary>
    /// The guard that did the work all along is untouched: a trimmed right prompt with the cursor
    /// somewhere else on the line still refuses, because the cursor is not at the end.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_WhenARightPromptWasTrimmedAndTheCursorIsMidLine_StillRefuses()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st", cursorOffset: 3, rightPromptTrimmed: true),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// The V1 desync cases, stated as the planner sees them. Each is a line the shell rewrote
    /// without emitting anything a keystroke mirror could observe; each now produces the delta the
    /// grid implies rather than the delta a stale mirror implied.
    /// </summary>
    [Theory]
    // Ctrl+U: the mirror still held "git st"; V1 would have sent "atus" onto an empty line.
    [InlineData("", "git status", "git status")]
    // Up-arrow history recall: the line is now a full command the user never typed a character of.
    [InlineData("git status", "git status --short", " --short")]
    // Shell-side Tab completion: the shell finished the word for the user.
    [InlineData("git status", "git status --porcelain", " --porcelain")]
    public void TryCreateInsertion_AfterTheShellRewroteTheLine_FollowsTheGrid(
        string lineAfterShellEdit,
        string selectedCommand,
        string expected)
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(lineAfterShellEdit),
            selectedCommand,
            out string? textToSend);

        Assert.True(created);
        Assert.Equal(expected, textToSend);
    }

    // ------------------------------------------- inline predictions (dogfood round 4, item 1)

    /// <summary>
    /// A line the reader classified as "typed prefix, then a ghost": the whole painted line, the cursor
    /// at the end of the typed part, and the flag set.
    /// </summary>
    private static AssistQuerySnapshot Ghosted(string typed, string ghost) =>
        new(typed + ghost, typed.Length, IsMultiline: false, RightPromptTrimmed: false, TextAfterCursorIsGhost: true);

    /// <summary>
    /// The bug the owner hit, at the layer it is fixed. With a prediction painted past the cursor the
    /// line reads as usable again, because a prediction is not text the user typed - it lives in the
    /// shell's suggestion buffer, never in its input buffer, and the next keystroke recomputes it.
    /// </summary>
    [Fact]
    public void GhostSuffix_MakesTheLineUsableAsATypedPrefix()
    {
        AssistQuerySnapshot line = Ghosted("docke", "r ps -a");

        Assert.True(line.IsUsableAsTypedPrefix);
        Assert.Equal("docke", line.TypedPrefix);
    }

    /// <summary>
    /// And the delta is computed against the typed characters, not against the whole painted line. This
    /// is the assertion that catches the half-fix: flip <c>TypedPrefix</c> back to <c>Text</c> in the
    /// planner and the suffix comes out empty or wrong, because the prediction the shell guessed is not
    /// the suggestion the user picked.
    /// </summary>
    [Fact]
    public void GhostSuffix_IsMeasuredAgainstTheTypedCharactersOnly()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Ghosted("docke", "r ps -a"),
            selectedCommand: "docker compose up",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("r compose up", textToSend);
    }

    /// <summary>
    /// One typed character and a full-line prediction - the shape a fresh prompt produces on the first
    /// keystroke, and the one that used to refuse hardest.
    /// </summary>
    [Fact]
    public void GhostSuffix_WorksFromASingleTypedCharacter()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Ghosted("d", "ocker compose up"),
            selectedCommand: "docker ps",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("ocker ps", textToSend);
    }

    /// <summary>
    /// The typed part still has to be a prefix of the suggestion. The ghost widens which lines are
    /// readable; it does not weaken what makes an append correct.
    /// </summary>
    [Fact]
    public void GhostSuffix_StillRefusesWhenTheTypedTextIsNotAPrefix()
    {
        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(
            Ghosted("kubect", "l get pods"),
            selectedCommand: "docker ps",
            out string? textToSend));

        Assert.Null(textToSend);
    }

    /// <summary>
    /// A multiline entry is refused whatever the flag says. The two facts are independent - the flag
    /// describes the region past the cursor, and a continuation prompt is text before it that the user
    /// never typed - so the multiline term is kept as its own conjunct rather than folded in.
    /// </summary>
    [Fact]
    public void GhostSuffix_DoesNotOverrideTheMultilineRefusal()
    {
        var line = new AssistQuerySnapshot(
            "git commit\n> ",
            CursorOffset: 10,
            IsMultiline: true,
            RightPromptTrimmed: false,
            TextAfterCursorIsGhost: true);

        Assert.False(line.IsUsableAsTypedPrefix);
    }

    /// <summary>
    /// Without the flag nothing changes: a mid-line cursor refuses exactly as it did before this round.
    /// The flag is the reader's proof, and no proof means no licence.
    /// </summary>
    [Fact]
    public void WithoutTheGhostFlag_AMidLineCursorStillRefuses()
    {
        AssistQuerySnapshot line = Line("docker ps -a", cursorOffset: 5);

        Assert.False(line.IsUsableAsTypedPrefix);
        Assert.Equal("docker ps -a", line.TypedPrefix);
        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(line, "docker compose up", out _));
    }
}
