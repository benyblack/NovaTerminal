using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistModeRouterTests
{
    [Fact]
    public void CommandAssistMode_DefinesExpectedHelperModes()
    {
        Assert.Equal(CommandAssistMode.Suggest, Enum.Parse<CommandAssistMode>("Suggest"));
        Assert.Equal(CommandAssistMode.Search, Enum.Parse<CommandAssistMode>("Search"));
        Assert.Equal(CommandAssistMode.Help, Enum.Parse<CommandAssistMode>("Help"));
        Assert.Equal(CommandAssistMode.Fix, Enum.Parse<CommandAssistMode>("Fix"));
    }

    [Fact]
    public void CommandAssistContextSnapshot_CapturesPaneScopedContext()
    {
        var snapshot = new CommandAssistContextSnapshot(
            QueryText: "git status",
            RecognizedCommand: "git",
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ProfileId: "profile-1",
            SessionId: "session-1",
            HostId: "host-1",
            IsRemote: false,
            SelectedText: "fatal: not a git repository");

        Assert.Equal("git", snapshot.RecognizedCommand);
        Assert.Equal("fatal: not a git repository", snapshot.SelectedText);
    }

    [Fact]
    public void ChooseModeForHelpRequest_ReturnsHelp()
    {
        var router = new CommandAssistModeRouter();

        CommandAssistMode mode = router.ChooseModeForHelpRequest();

        Assert.Equal(CommandAssistMode.Help, mode);
    }

    [Fact]
    public void ChooseMode_WhenFailureHasHighConfidence_ReturnsFix()
    {
        var router = new CommandAssistModeRouter();

        CommandAssistMode mode = router.ChooseModeForFailure(0.81);

        Assert.Equal(CommandAssistMode.Fix, mode);
    }

    [Fact]
    public void ChooseMode_WhenFailureHasLowConfidence_RemainsSuggest()
    {
        var router = new CommandAssistModeRouter();

        CommandAssistMode mode = router.ChooseModeForFailure(0.2);

        Assert.Equal(CommandAssistMode.Suggest, mode);
    }

    // ------------------------------------------------ UX-polish round: the Fix noise floor

    /// <summary>
    /// Anything a recogniser stood behind may surface, at every confidence the table publishes.
    /// </summary>
    /// <remarks>
    /// The 0.40 case is the one that matters and the one an earlier draft of this floor got wrong:
    /// <c>Explanatory</c> is what the table uses for a recognised failure with no single command to
    /// run, and <c>git status</c> outside a working tree - "This directory is not inside a Git
    /// repository" - is exactly what the feature is for.
    /// </remarks>
    [Theory]
    [InlineData(0.95)]
    [InlineData(0.7)]
    [InlineData(0.55)]
    [InlineData(0.4)]
    public void ShouldSurfacePassiveFix_ForAnythingTheTableRecognized_IsTrue(double confidence)
    {
        var router = new CommandAssistModeRouter();

        Assert.True(router.ShouldSurfacePassiveFix(
            [Fix("This directory is not inside a Git repository", confidence, recognizerId: "git-not-a-repository")]));
    }

    /// <summary>
    /// An uninformed guess does not interrupt however confident it is about itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the mutation check for the floor.</strong> Dropping <c>IsRecognized</c> from
    /// <c>ShouldSurfacePassiveFix</c> lets every one of these back onto the screen and fails here.
    /// </para>
    /// <para>
    /// The 0.70 row is the important one: it is the no-output name-similarity guess, priced above any
    /// confidence threshold that would still admit a real explanation, which is why provenance rather
    /// than confidence is the gate.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0.7)]
    [InlineData(0.55)]
    [InlineData(0.4)]
    public void ShouldSurfacePassiveFix_ForAnUnrecognizedGuess_IsFalse(double confidence)
    {
        var router = new CommandAssistModeRouter();

        Assert.False(router.ShouldSurfacePassiveFix([Fix("Did you mean git?", confidence, recognizerId: null)]));
    }

    /// <summary>
    /// One recognised row carries a list that also holds guesses.
    /// </summary>
    [Fact]
    public void ShouldSurfacePassiveFix_WhenAnyRowWasRecognized_IsTrue()
    {
        var router = new CommandAssistModeRouter();

        Assert.True(router.ShouldSurfacePassiveFix(
        [
            Fix("Did you mean git?", 0.7, recognizerId: null),
            Fix("Run it with ./", 0.7, recognizerId: "command-not-found")
        ]));
    }

    /// <summary>
    /// A list of nothing but guesses stays down, however many of them there are.
    /// </summary>
    [Fact]
    public void ShouldSurfacePassiveFix_WhenEveryRowIsAGuess_IsFalse()
    {
        var router = new CommandAssistModeRouter();

        Assert.False(router.ShouldSurfacePassiveFix(
        [
            Fix("Did you mean git?", 0.7, recognizerId: null),
            Fix("Did you mean gh?", 0.55, recognizerId: null)
        ]));
    }

    [Fact]
    public void ShouldSurfacePassiveFix_WithNoInsights_IsFalse()
    {
        var router = new CommandAssistModeRouter();

        Assert.False(router.ShouldSurfacePassiveFix([]));
    }

    private static CommandFixSuggestion Fix(string title, double confidence, string? recognizerId)
    {
        return new CommandFixSuggestion(
            Title: title,
            SuggestedCommand: "git status",
            Description: null,
            Confidence: confidence,
            Badges: ["Fix"],
            RecognizerId: recognizerId);
    }

    [Theory]
    [InlineData("git status", "git")]
    [InlineData("Get-ChildItem -Force", "Get-ChildItem")]
    [InlineData("\"C:/Program Files/Git/bin/git.exe\" status", "C:/Program Files/Git/bin/git.exe")]
    public void ParsePrimaryCommand_WhenCommandIsSimple_ReturnsLeadingToken(string input, string expected)
    {
        string? token = RecognizedCommandParser.ParsePrimaryCommand(input);

        Assert.Equal(expected, token);
    }

    [Fact]
    public void ParsePrimaryCommand_WhenInputIsBlank_ReturnsNull()
    {
        string? token = RecognizedCommandParser.ParsePrimaryCommand("   ");

        Assert.Null(token);
    }
}
