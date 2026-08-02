using Avalonia.Input;
using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Controls
{
    /// <summary>
    /// Translates Avalonia input types into the Avalonia-free vocabulary Command Assist uses.
    /// </summary>
    /// <remarks>
    /// This is the single App-side boundary between <c>Avalonia.Input</c> and
    /// <c>NovaTerminal.CommandAssist</c>; the assist assembly must not reference Avalonia
    /// (enforced by <c>CommandAssist_must_not_depend_on_Avalonia</c> in the architecture tests).
    /// </remarks>
    internal static class AssistKeyMapper
    {
        internal static AssistKey ToAssistKey(Key key) => key switch
        {
            Key.Escape => AssistKey.Escape,
            Key.Up => AssistKey.Up,
            Key.Down => AssistKey.Down,
            Key.Enter => AssistKey.Enter,
            Key.Tab => AssistKey.Tab,
            Key.P => AssistKey.P,
            _ => AssistKey.None,
        };

        internal static AssistModifiers ToAssistModifiers(KeyModifiers modifiers)
        {
            AssistModifiers result = AssistModifiers.None;
            if ((modifiers & KeyModifiers.Alt) != 0)
            {
                result |= AssistModifiers.Alt;
            }

            if ((modifiers & KeyModifiers.Control) != 0)
            {
                result |= AssistModifiers.Control;
            }

            if ((modifiers & KeyModifiers.Shift) != 0)
            {
                result |= AssistModifiers.Shift;
            }

            return result;
        }
    }
}
