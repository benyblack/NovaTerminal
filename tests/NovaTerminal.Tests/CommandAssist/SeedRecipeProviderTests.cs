using NovaTerminal.CommandAssist.Domain;
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

    [Fact]
    public async Task GetRecipesAsync_WhenCommandTokenDoesNotMatch_ReturnsEmptyList()
    {
        var provider = new SeedRecipeProvider();
        var query = new CommandHelpQuery(
            RawInput: "kubectl get pods",
            CommandToken: "kubectl",
            ShellKind: "bash",
            WorkingDirectory: "/repo",
            SelectedText: null,
            SessionId: null);

        IReadOnlyList<CommandHelpItem> result = await provider.GetRecipesAsync(query, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecipesAsync_WhenShellMatches_PrefersShellSpecificRecipes()
    {
        var provider = new SeedRecipeProvider();
        var query = new CommandHelpQuery(
            RawInput: "Get-ChildItem",
            CommandToken: "Get-ChildItem",
            ShellKind: "pwsh",
            WorkingDirectory: "C:/repo",
            SelectedText: null,
            SessionId: null);

        IReadOnlyList<CommandHelpItem> result = await provider.GetRecipesAsync(query, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal("pwsh", result[0].ShellKind);
    }
}
