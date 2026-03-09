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
}
