using Avalonia.Input;

namespace NovaTerminal.CommandAssist.Application;

internal static class CommandAssistKeyRouter
{
    internal static bool IsAssistOwnedKey(bool isAssistVisible, Key key, KeyModifiers modifiers)
    {
        if (!isAssistVisible)
        {
            return false;
        }

        bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
        bool isShift = (modifiers & KeyModifiers.Shift) != 0;

        return key == Key.Escape ||
               key == Key.Up ||
               key == Key.Down ||
               key == Key.Tab ||
               (isCtrl && isShift && key == Key.P);
    }
}
