using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void GetDefinitions_IncludesSettingsPaneAndCommandAssistBindings()
    {
        IReadOnlyList<ShortcutDefinition> definitions = ShortcutCatalog.GetDefinitions();

        Assert.Contains(definitions, definition => definition.CommandId == "settings" && definition.Scope == ShortcutScope.App);
        Assert.Contains(definitions, definition => definition.CommandId == "command_assist_toggle" && definition.Scope == ShortcutScope.CommandAssist);
        Assert.Contains(definitions, definition => definition.CommandId == "find" && definition.Scope == ShortcutScope.Pane);
    }

    [Fact]
    public void GetEntries_ExposesDisplayMetadataForSettingsBinding()
    {
        ShortcutCatalogEntry settingsEntry = Assert.Single(
            ShortcutCatalog.GetEntries(),
            entry => entry.CommandId == "settings");

        Assert.Equal("Settings", settingsEntry.Title);
        Assert.Equal("General", settingsEntry.Category);
        Assert.Equal("Ctrl+,", settingsEntry.DefaultBinding);
    }

    [Fact]
    public void GetEntries_IncludesMoveTabShortcuts_WithPageKeyDefaults()
    {
        ShortcutCatalogEntry prev = Assert.Single(
            ShortcutCatalog.GetEntries(),
            entry => entry.CommandId == "move_tab_prev");
        ShortcutCatalogEntry next = Assert.Single(
            ShortcutCatalog.GetEntries(),
            entry => entry.CommandId == "move_tab_next");

        Assert.Equal("Tab: Move Previous", prev.Title);
        Assert.Equal("Tab: Move Next", next.Title);
        Assert.Equal("General", prev.Category);
        Assert.Equal("General", next.Category);
        Assert.Equal(ShortcutScope.App, prev.Scope);
        Assert.Equal(ShortcutScope.App, next.Scope);
        Assert.Equal("Ctrl+Shift+PageUp", prev.DefaultBinding);
        Assert.Equal("Ctrl+Shift+PageDown", next.DefaultBinding);
    }
}
