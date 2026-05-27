using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Core.Shortcuts;

public static class ShortcutCatalog
{
    private static readonly IReadOnlyList<ShortcutCatalogEntry> Entries =
    [
        new("command_palette", "Command Palette", "General", ShortcutScope.App, "Ctrl+Shift+P"),
        new("settings", "Settings", "General", ShortcutScope.App, "Ctrl+,"),
        new("new_tab", "New Tab", "General", ShortcutScope.App, "Ctrl+Shift+T"),
        new("close_tab", "Close Tab", "General", ShortcutScope.App, "Ctrl+W"),
        new("next_tab", "Tab: Next (MRU)", "General", ShortcutScope.App, "Ctrl+Tab"),
        new("prev_tab", "Tab: Previous (MRU)", "General", ShortcutScope.App, "Ctrl+Shift+Tab"),
        new("open_tab_list", "Tab: Open Tab List", "General", ShortcutScope.App, "Ctrl+Shift+O"),
        new("font_increase", "Font: Increase", "View", ShortcutScope.App, "Ctrl+OemPlus"),
        new("font_increase_alt", "Font: Increase (Alt)", "View", ShortcutScope.App, "Ctrl+Add"),
        new("font_decrease", "Font: Decrease", "View", ShortcutScope.App, "Ctrl+OemMinus"),
        new("font_decrease_alt", "Font: Decrease (Alt)", "View", ShortcutScope.App, "Ctrl+Subtract"),
        new("split_vertical", "Split Vertical", "View", ShortcutScope.Pane, "Ctrl+Shift+D"),
        new("split_horizontal", "Split Horizontal", "View", ShortcutScope.Pane, "Ctrl+Shift+E"),
        new("equalize_panes", "Equalize Panes", "View", ShortcutScope.Pane, "Ctrl+Shift+G"),
        new("toggle_pane_zoom", "Pane: Toggle Zoom", "View", ShortcutScope.Pane, "Ctrl+Shift+Z"),
        new("toggle_broadcast_input", "Pane: Toggle Broadcast Input (Tab)", "View", ShortcutScope.Pane, "Ctrl+Shift+B"),
        new("find", "Find in Terminal", "Edit", ShortcutScope.Pane, "Ctrl+F"),
        new("find_alt", "Find in Terminal (Alt)", "Edit", ShortcutScope.Pane, "Ctrl+Shift+F"),
        new("close_pane", "Close Pane", "General", ShortcutScope.Pane, "Ctrl+Shift+W"),
        new("paste", "Paste", "Edit", ShortcutScope.Pane, "Ctrl+V"),
        new("command_assist_toggle", "Command Assist Toggle", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Space"),
        new("command_assist_help", "Command Assist Help", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Shift+H"),
        new("command_assist_history", "Command Assist History", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+R"),
    ];

    public static IReadOnlyList<ShortcutDefinition> GetDefinitions()
    {
        return Entries
            .Select(entry => new ShortcutDefinition(entry.CommandId, entry.Scope, entry.DefaultBinding))
            .ToArray();
    }

    public static IReadOnlyList<ShortcutCatalogEntry> GetEntries()
    {
        return Entries;
    }
}
