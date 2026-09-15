using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Shell;

public static class CommandPaletteOrdering
{
    public static IReadOnlyList<TerminalCommand> OrderForEmptyQuery(
        IEnumerable<TerminalCommand> commands,
        IReadOnlyDictionary<string, CommandPaletteUsageEntry> usage)
    {
        return OrderCommands(commands, usage);
    }

    public static IReadOnlyList<TerminalCommand> OrderSearchResults(
        IEnumerable<TerminalCommand> commands,
        IReadOnlyDictionary<string, CommandPaletteUsageEntry> usage)
    {
        return OrderCommands(commands, usage);
    }

    private static int GetUseCount(IReadOnlyDictionary<string, CommandPaletteUsageEntry> usage, string commandId)
    {
        return usage.TryGetValue(commandId, out CommandPaletteUsageEntry? entry) ? entry.UseCount : 0;
    }

    private static DateTimeOffset GetLastUsedAt(IReadOnlyDictionary<string, CommandPaletteUsageEntry> usage, string commandId)
    {
        return usage.TryGetValue(commandId, out CommandPaletteUsageEntry? entry)
            ? entry.LastUsedAt
            : DateTimeOffset.MinValue;
    }

    private static IReadOnlyList<TerminalCommand> OrderCommands(
        IEnumerable<TerminalCommand> commands,
        IReadOnlyDictionary<string, CommandPaletteUsageEntry> usage)
    {
        usage ??= new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase);

        return commands
            .OrderByDescending(command => GetUseCount(usage, command.Id))
            .ThenByDescending(command => GetLastUsedAt(usage, command.Id))
            .ThenBy(command => command.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(command => command.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
