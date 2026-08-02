namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Decides whether a keystroke belongs to Command Assist or should fall through to the terminal.
/// </summary>
/// <remarks>
/// Public because the App's <c>TerminalPane</c> consults it on every key press. It is a pure
/// static predicate over public types (<see cref="AssistKey"/>, <see cref="AssistModifiers"/>),
/// so exposing it costs nothing and lets this assembly avoid granting the App
/// <c>InternalsVisibleTo</c>.
/// </remarks>
public static class CommandAssistKeyRouter
{
    public static bool IsAssistOwnedKey(bool isAssistVisible, AssistKey key, AssistModifiers modifiers)
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
