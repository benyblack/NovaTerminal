using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class HeuristicErrorInsightServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_WhenCommandNotFound_ReturnsHighConfidenceFix()
    {
        var service = new HeuristicErrorInsightService();
        var context = new CommandFailureContext(
            CommandText: "gti status",
            ExitCode: 127,
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            ErrorOutput: "command not found: gti",
            IsRemote: false,
            SelectedText: null);

        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

        Assert.Contains(result, item => item.Confidence >= 0.8);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenPowerShellCommandNotRecognized_SuggestsLikelyCommand()
    {
        var service = new HeuristicErrorInsightService();
        var context = new CommandFailureContext(
            CommandText: "Get-ChldItem",
            ExitCode: 1,
            ShellKind: "pwsh",
            WorkingDirectory: "C:/repo",
            ErrorOutput: "The term 'Get-ChldItem' is not recognized",
            IsRemote: false,
            SelectedText: null);

        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

        Assert.Contains(result, item => item.SuggestedCommand.Contains("Get-ChildItem", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_WhenWindowsCommandIsNotRecognized_SuggestsLikelyCommand()
    {
        var service = new HeuristicErrorInsightService();
        var context = new CommandFailureContext(
            CommandText: "gti status",
            ExitCode: 1,
            ShellKind: "cmd",
            WorkingDirectory: @"C:\repo",
            ErrorOutput: "'gti' is not recognized as an internal or external command",
            IsRemote: false,
            SelectedText: null);

        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

        Assert.Contains(result, item => item.SuggestedCommand.StartsWith("git ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_WhenNoSuchFileOrDirectory_SuggestsCurrentDirectoryInvocation()
    {
        var service = new HeuristicErrorInsightService();
        var context = new CommandFailureContext(
            CommandText: "build.sh",
            ExitCode: 127,
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            ErrorOutput: "No such file or directory",
            IsRemote: false,
            SelectedText: null);

        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

        Assert.Contains(result, item => item.SuggestedCommand == "./build.sh");
    }

    [Fact]
    public async Task AnalyzeAsync_WhenFailureIsLowConfidence_DoesNotReturnAutoOpenWorthySuggestion()
    {
        var service = new HeuristicErrorInsightService();
        var context = new CommandFailureContext(
            CommandText: "invoke-something --mystery",
            ExitCode: 1,
            ShellKind: "pwsh",
            WorkingDirectory: "C:/repo",
            ErrorOutput: "operation failed",
            IsRemote: false,
            SelectedText: null);

        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

        Assert.DoesNotContain(result, item => item.Confidence >= 0.8);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenCommandAlreadyMatchesKnownToken_DoesNotReturnTypoFix()
    {
        var service = new HeuristicErrorInsightService();
        var context = new CommandFailureContext(
            CommandText: "git commit",
            ExitCode: 1,
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            ErrorOutput: null,
            IsRemote: false,
            SelectedText: null);

        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

        Assert.DoesNotContain(result, item => item.Badges?.Contains("Typo", StringComparer.Ordinal) == true);
    }
}
