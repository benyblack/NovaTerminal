using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class SeedRecipeProvider : IRecipeProvider
{
    private static readonly IReadOnlyList<(string CommandToken, CommandHelpItem Item)> Recipes =
    [
        ("git", new CommandHelpItem("Clone and switch", "git checkout -b feature/m4", "Create and switch to a feature branch.", "bash", ["Recipe"])),
        ("git", new CommandHelpItem("Inspect working tree", "git status --short", "Show concise repository state.", "bash", ["Recipe"])),
        ("docker", new CommandHelpItem("Inspect containers", "docker ps --format \"table {{.Names}}\\t{{.Status}}\"", "List container names and status.", "bash", ["Recipe"])),
        ("ls", new CommandHelpItem("List with details", "ls -lah", "Show hidden files with human-readable sizes.", "bash", ["Recipe"])),
        ("grep", new CommandHelpItem("Recursive search", "grep -R \"TODO\" .", "Search recursively for matching text.", "bash", ["Recipe"])),
        ("Get-ChildItem", new CommandHelpItem("PowerShell directory listing", "Get-ChildItem -Force", "Show hidden items in the current directory.", "pwsh", ["Recipe"])),
        ("Set-Location", new CommandHelpItem("PowerShell navigation", "Set-Location C:/repo", "Change to a repository directory.", "pwsh", ["Recipe"]))
    ];

    public Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query.CommandToken))
        {
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>(Array.Empty<CommandHelpItem>());
        }

        IReadOnlyList<CommandHelpItem> ordered = Recipes
            .Where(recipe => string.Equals(recipe.CommandToken, query.CommandToken, StringComparison.OrdinalIgnoreCase))
            .Select(recipe => recipe.Item)
            .OrderByDescending(item => ShellMatches(item.ShellKind, query.ShellKind))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(ordered);
    }

    private static bool ShellMatches(string? recipeShellKind, string? queryShellKind)
    {
        if (string.IsNullOrWhiteSpace(recipeShellKind) || string.IsNullOrWhiteSpace(queryShellKind))
        {
            return false;
        }

        return string.Equals(recipeShellKind, queryShellKind, StringComparison.OrdinalIgnoreCase);
    }
}
