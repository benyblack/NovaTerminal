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
}
