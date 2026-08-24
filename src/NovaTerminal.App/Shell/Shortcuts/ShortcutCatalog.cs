using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.TitleBar;

namespace NovaTerminal.Shell.Shortcuts;

public static class ShortcutCatalog
{
    private static readonly IReadOnlyList<ShortcutCatalogEntry> Entries =
    [
        new("command_palette", "Command Palette", "General", ShortcutScope.App, "Ctrl+Shift+P"),
        new("settings", "Settings", "General", ShortcutScope.App, "Ctrl+,"),
        new("connections", "Connection Manager", "General", ShortcutScope.App, "Ctrl+Shift+K"),
        new("new_tab", "New Tab", "General", ShortcutScope.App, "Ctrl+Shift+T"),
        new("close_tab", "Close Tab", "General", ShortcutScope.App, "Ctrl+W"),
        new("next_tab", "Tab: Next (MRU)", "General", ShortcutScope.App, "Ctrl+Tab"),
        new("prev_tab", "Tab: Previous (MRU)", "General", ShortcutScope.App, "Ctrl+Shift+Tab"),
        new(TitleBarCatalog.OpenTabListId, "Tab: Open Tab List", "General", ShortcutScope.App, "Ctrl+Shift+O"),
        new("font_increase", "Font: Increase", "View", ShortcutScope.App, "Ctrl+OemPlus"),
        new("font_increase_alt", "Font: Increase (Alt)", "View", ShortcutScope.App, "Ctrl+Add"),
        new("font_decrease", "Font: Decrease", "View", ShortcutScope.App, "Ctrl+OemMinus"),
        new("font_decrease_alt", "Font: Decrease (Alt)", "View", ShortcutScope.App, "Ctrl+Subtract"),
        new("split_vertical", "Split Vertical", "View", ShortcutScope.Pane, "Ctrl+Shift+D"),
        new("split_horizontal", "Split Horizontal", "View", ShortcutScope.Pane, "Ctrl+Shift+E"),
        new("equalize_panes", "Equalize Panes", "View", ShortcutScope.Pane, "Ctrl+Shift+G"),
        new("toggle_pane_zoom", "Pane: Toggle Zoom", "View", ShortcutScope.Pane, "Ctrl+Shift+Z"),
        new("toggle_broadcast_input", "Pane: Toggle Broadcast Input (Tab)", "View", ShortcutScope.Pane, "Ctrl+Shift+B"),
        new("toggle_recording", "Toggle Recording", "Pane", ShortcutScope.Pane, "Ctrl+Shift+R"),
        new("find", "Find in Terminal", "Edit", ShortcutScope.Pane, "Ctrl+F"),
        new("find_alt", "Find in Terminal (Alt)", "Edit", ShortcutScope.Pane, "Ctrl+Shift+F"),
        new("close_pane", "Close Pane", "General", ShortcutScope.Pane, "Ctrl+Shift+W"),
        new("paste", "Paste", "Edit", ShortcutScope.Pane, "Ctrl+V"),
        new("command_assist_toggle", "Command Assist Toggle", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Space"),
        new("command_assist_help", "Command Assist Help", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Shift+H"),
        new("command_assist_history", "Command Assist History", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+R"),

        // V2 Phase 3b: pin moves off Ctrl+Shift+P, which is the command palette's. The two shared it
        // by MainWindow trying the pin first and falling through, so whether the palette opened
        // depended on whether an assist row was selected. Ctrl+Shift+S ("snippet" - pinning is what
        // creates one) is free in this catalogue and, unlike an Alt chord, reliably reaches the
        // window: TerminalView sends Alt+<key> to the PTY as an ESC-prefixed sequence and marks the
        // event handled, so a Ctrl+Alt+P binding would never bubble this far.
        new("command_assist_pin", "Command Assist: Pin/Unpin Selection", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Shift+S"),

        // The in-surface keys. Unlike every other entry here these are not dispatched from
        // MainWindow's shortcut handler - CommandAssistKeyRouter consumes them inside the pane, from
        // bindings resolved through AssistShortcutBindingResolver - but they are catalogued for the
        // same two reasons: the user can rebind them, and the bubble's hint strip reads its key names
        // from here instead of hard-coding them.
        //
        // Rebinding these is constrained in a way the catalogue cannot express: Command Assist models
        // Escape/Up/Down/Enter/Tab only (AssistKey), so an override naming any other key falls back to
        // the default rather than silently matching nothing.
        new("command_assist_dismiss", "Command Assist: Close", "Command Assist", ShortcutScope.CommandAssist, "Escape"),
        new("command_assist_selection_up", "Command Assist: Previous Suggestion", "Command Assist", ShortcutScope.CommandAssist, "Up"),
        new("command_assist_selection_down", "Command Assist: Next Suggestion", "Command Assist", ShortcutScope.CommandAssist, "Down"),
        new("command_assist_accept", "Command Assist: Accept While Browsing", "Command Assist", ShortcutScope.CommandAssist, "Enter"),
        new("command_assist_insert", "Command Assist: Insert Selection", "Command Assist", ShortcutScope.CommandAssist, "Ctrl+Enter"),
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
