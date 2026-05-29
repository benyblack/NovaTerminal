using NovaTerminal.Shell;
using NovaTerminal.Core;
using NovaTerminal.VT;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class CommandPaletteOrderingTests
{
    [Fact]
    public void OrderForEmptyQuery_PrefersMostUsedCommands()
    {
        TerminalCommand[] commands =
        [
            new TerminalCommand { Id = "settings", Title = "Settings", Category = "General" },
            new TerminalCommand { Id = "open_recording", Title = "Open Recording...", Category = "General" },
        ];

        Dictionary<string, CommandPaletteUsageEntry> usage = new(StringComparer.OrdinalIgnoreCase)
        {
            ["settings"] = new("settings", 8, DateTimeOffset.UtcNow),
            ["open_recording"] = new("open_recording", 1, DateTimeOffset.UtcNow),
        };

        IReadOnlyList<TerminalCommand> ordered = CommandPaletteOrdering.OrderForEmptyQuery(commands, usage);

        Assert.Equal("settings", ordered[0].Id);
    }

    [Fact]
    public void OrderForEmptyQuery_BreaksUsageTiesByMostRecentUse()
    {
        TerminalCommand[] commands =
        [
            new() { Id = "settings", Title = "Settings", Category = "General" },
            new() { Id = "new_tab", Title = "New Tab", Category = "General" },
        ];

        Dictionary<string, CommandPaletteUsageEntry> usage = new(StringComparer.OrdinalIgnoreCase)
        {
            ["settings"] = new("settings", 3, new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero)),
            ["new_tab"] = new("new_tab", 3, new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero)),
        };

        IReadOnlyList<TerminalCommand> ordered = CommandPaletteOrdering.OrderForEmptyQuery(commands, usage);

        Assert.Equal("new_tab", ordered[0].Id);
    }

    [Fact]
    public void OrderForEmptyQuery_FallsBackToCategoryAndTitleWhenUnused()
    {
        TerminalCommand[] commands =
        [
            new() { Id = "split_vertical", Title = "Split Vertical", Category = "View" },
            new() { Id = "settings", Title = "Settings", Category = "General" },
            new() { Id = "close_tab", Title = "Close Tab", Category = "General" },
        ];

        IReadOnlyList<TerminalCommand> ordered = CommandPaletteOrdering.OrderForEmptyQuery(
            commands,
            new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["close_tab", "settings", "split_vertical"], ordered.Select(command => command.Id).ToArray());
    }

    [Fact]
    public void OrderSearchResults_PrefersMostUsedCommandsWhenMatchesTie()
    {
        TerminalCommand[] commands =
        [
            new() { Id = "settings", Title = "Settings", Category = "General" },
            new() { Id = "split_settings", Title = "Split Settings", Category = "View" },
        ];

        Dictionary<string, CommandPaletteUsageEntry> usage = new(StringComparer.OrdinalIgnoreCase)
        {
            ["split_settings"] = new("split_settings", 5, new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero)),
            ["settings"] = new("settings", 1, new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero)),
        };

        IReadOnlyList<TerminalCommand> ordered = CommandPaletteOrdering.OrderSearchResults(commands, usage);

        Assert.Equal("split_settings", ordered[0].Id);
    }
}
