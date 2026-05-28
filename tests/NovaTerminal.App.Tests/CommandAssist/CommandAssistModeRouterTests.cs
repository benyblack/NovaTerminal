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
