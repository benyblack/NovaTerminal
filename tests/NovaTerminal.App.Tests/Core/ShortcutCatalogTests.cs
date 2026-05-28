using NovaTerminal.Core.Shortcuts;

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
}
