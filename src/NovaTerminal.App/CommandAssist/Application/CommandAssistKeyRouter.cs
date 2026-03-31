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
        bool isAlt = (modifiers & KeyModifiers.Alt) != 0;

        return key == Key.Escape ||
               key == Key.Up ||
               key == Key.Down ||
               (isCtrl && !isShift && !isAlt && key == Key.Enter) ||
               (isCtrl && isShift && key == Key.P);
    }
}
