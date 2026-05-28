using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistInsertionPlannerTests
{
    [Fact]
    public void TryCreateInsertion_WhenQueryIsPrefix_ReturnsOnlySuffix()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            existingQuery: "git st",
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("atus", textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenQueryMatchesExactly_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            existingQuery: "git status",
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenQueryIsEmpty_ReturnsFullSuggestion()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            existingQuery: string.Empty,
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("git status", textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenSuggestionDoesNotStartWithQuery_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            existingQuery: "kubectl",
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }
}
