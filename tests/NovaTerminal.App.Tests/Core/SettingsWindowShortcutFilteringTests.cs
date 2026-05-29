using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class SettingsWindowShortcutFilteringTests
{
    [Fact]
    public void FilterShortcutCatalogEntries_MatchesTitleCategoryAndScope()
    {
        IReadOnlyList<ShortcutCatalogEntry> results = SettingsWindow.FilterShortcutCatalogEntries("assist");

        Assert.Contains(results, entry => entry.CommandId == "command_assist_toggle");
        Assert.DoesNotContain(results, entry => entry.CommandId == "settings");
    }
}
