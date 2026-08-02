using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class LocalCommandDocsProvider : ICommandDocsProvider
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CommandHelpItem>> HelpByCommand =
        new Dictionary<string, IReadOnlyList<CommandHelpItem>>(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new[]
            {
                new CommandHelpItem("git basics", "git status", "Inspect repository state.", "bash", new[] { "Doc" }),
                new CommandHelpItem("git checkout", "git checkout <branch>", "Switch branches or restore files.", "bash", new[] { "Doc" })
            },
            ["docker"] = new[]
            {
                new CommandHelpItem("docker basics", "docker ps", "List running containers.", "bash", new[] { "Doc" })
            },
            ["ls"] = new[]
            {
                new CommandHelpItem("ls listing", "ls -la", "List files including hidden entries.", "bash", new[] { "Doc" })
            },
            ["cd"] = new[]
            {
                new CommandHelpItem("cd navigation", "cd /path/to/dir", "Change the current directory.", "bash", new[] { "Doc" })
            },
            ["grep"] = new[]
            {
                new CommandHelpItem("grep search", "grep -R \"pattern\" .", "Search recursively for text.", "bash", new[] { "Doc" })
            },
            ["Get-ChildItem"] = new[]
            {
                new CommandHelpItem("Get-ChildItem basics", "Get-ChildItem", "List child items in the current location.", "pwsh", new[] { "Doc" })
            },
            ["Set-Location"] = new[]
            {
                new CommandHelpItem("Set-Location basics", "Set-Location C:/repo", "Change the current location.", "pwsh", new[] { "Doc" })
            }
        };

    public Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(CommandHelpQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query.CommandToken) ||
            !HelpByCommand.TryGetValue(query.CommandToken, out IReadOnlyList<CommandHelpItem>? helpItems))
        {
            return Task.FromResult<IReadOnlyList<CommandHelpItem>>(Array.Empty<CommandHelpItem>());
        }

        IReadOnlyList<CommandHelpItem> ordered = helpItems
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(ordered);
    }
}
