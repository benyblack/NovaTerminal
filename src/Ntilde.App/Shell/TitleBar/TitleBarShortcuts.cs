using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// Shortcut labels for title bar tooltips and settings rows. Defaults come from
/// <see cref="ShortcutCatalog"/> rather than being restated in the title bar catalog, so the two
/// cannot drift apart.
/// </summary>
public static class TitleBarShortcuts
{
    public static string Resolve(string shortcutKey, IReadOnlyDictionary<string, string>? keybindings)
    {
        if (string.IsNullOrWhiteSpace(shortcutKey))
        {
            return string.Empty;
        }

        if (keybindings is not null &&
            keybindings.TryGetValue(shortcutKey, out string? custom) &&
            !string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        return ShortcutCatalog.GetEntries()
            .FirstOrDefault(e => e.CommandId == shortcutKey)?.DefaultBinding
            ?? string.Empty;
    }

    public static string FormatTooltip(string title, string shortcut)
        => string.IsNullOrWhiteSpace(shortcut) ? title : $"{title} ({shortcut})";
}
