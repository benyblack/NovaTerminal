using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class SeedRecipeProviderTests
{
    [Fact]
    public async Task GetRecipesAsync_WhenCommandTokenMatches_ReturnsSeededRecipes()
    {
        var provider = new SeedRecipeProvider();
        var query = new CommandHelpQuery(
            RawInput: "git checkout",
            CommandToken: "git",
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            SelectedText: null,
            SessionId: null);

        IReadOnlyList<CommandHelpItem> result = await provider.GetRecipesAsync(query, CancellationToken.None);

        Assert.Contains(result, item => item.Command.Contains("git", StringComparison.OrdinalIgnoreCase));
    }
}
