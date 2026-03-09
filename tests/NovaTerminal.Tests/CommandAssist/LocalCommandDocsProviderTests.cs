using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class LocalCommandDocsProviderTests
{
    [Fact]
    public async Task GetHelpAsync_WhenCommandIsRecognized_ReturnsLocalHelp()
    {
        var provider = new LocalCommandDocsProvider();
        var query = new CommandHelpQuery(
            RawInput: "git checkout",
            CommandToken: "git",
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            SelectedText: null,
            SessionId: null);

        IReadOnlyList<CommandHelpItem> result = await provider.GetHelpAsync(query, CancellationToken.None);

        Assert.Contains(result, item => item.Title.Contains("git", StringComparison.OrdinalIgnoreCase));
    }
}
