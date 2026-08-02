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
    /// Refusal 4 - a right prompt was trimmed. The reader's RPROMPT heuristic is deliberately
    /// conservative, but conservative means "over-returns rather than deletes"; when it did fire,
    /// the tail of the line is an inference and the tail is exactly what a suffix append attaches
    /// to.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_WhenARightPromptWasTrimmed_Refuses()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st", rightPromptTrimmed: true),
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
}
