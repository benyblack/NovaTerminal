namespace NovaTerminal.CommandAssist.Application;

internal static class CommandAssistKeyRouter
{
    internal static bool IsAssistOwnedKey(bool isAssistVisible, AssistKey key, AssistModifiers modifiers)
    {
        if (!isAssistVisible)
        {
            return false;
        }

        bool isCtrl = (modifiers & AssistModifiers.Control) != 0;
        bool isShift = (modifiers & AssistModifiers.Shift) != 0;
        bool isAlt = (modifiers & AssistModifiers.Alt) != 0;

        return key == AssistKey.Escape ||
               key == AssistKey.Up ||
               key == AssistKey.Down ||
               (isCtrl && !isShift && !isAlt && key == AssistKey.Enter) ||
               (isCtrl && isShift && key == AssistKey.P);
    }
}
