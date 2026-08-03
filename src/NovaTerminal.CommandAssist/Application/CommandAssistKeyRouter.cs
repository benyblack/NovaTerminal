namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// What the assist surface is doing, reduced to the two facts key routing branches on.
/// </summary>
/// <remarks>
/// Passed as one value rather than as two bools so that adding a third fact does not silently change
/// the meaning of an existing call site's positional arguments - the router is consulted from the
/// App's key interceptor, which is the hottest path in the feature and the one place a wrong default
/// would be least visible.
/// </remarks>
/// <param name="IsSurfaceVisible">Whether any assist surface is on screen.</param>
/// <param name="IsAcceptOnEnterArmed">
/// Whether the session is in the browse state that makes an unmodified <c>Enter</c> mean "insert the
/// selected row" - see <see cref="AssistSessionStateMachine.AllowsAcceptOnEnter"/>, which is where
/// the rule lives. The router takes the answer rather than recomputing it: the state machine and the
/// view-model between them know whether the popup is open and whether a row is selected, and a second
/// implementation of that rule is a second thing to keep in sync.
/// </param>
public readonly record struct AssistKeyState(
    bool IsSurfaceVisible,
    bool IsAcceptOnEnterArmed);

/// <summary>
/// Decides whether a keystroke belongs to Command Assist or should fall through to the terminal.
/// </summary>
/// <remarks>
/// Public because the App's <c>TerminalPane</c> consults it on every key press. It is a pure
/// static predicate over public types (<see cref="AssistKey"/>, <see cref="AssistModifiers"/>,
/// <see cref="AssistKeyState"/>), so exposing it costs nothing and lets this assembly avoid granting
/// the App <c>InternalsVisibleTo</c>.
/// </remarks>
public static class CommandAssistKeyRouter
{
    public static bool IsAssistOwnedKey(AssistKeyState state, AssistKey key, AssistModifiers modifiers)
    {
        if (!state.IsSurfaceVisible)
        {
            return false;
        }

        bool isCtrl = (modifiers & AssistModifiers.Control) != 0;
        bool isShift = (modifiers & AssistModifiers.Shift) != 0;
        bool isAlt = (modifiers & AssistModifiers.Alt) != 0;

        // Unmodified Enter, and only while browsing (V2 Phase 3a). "Unmodified" is exact rather than
        // "no Alt": a modified Enter is encoded as CSI u under the kitty disambiguate tier and means
        // something to the shell's line editor, and Shift+Enter in particular is a newline in several
        // of them. Ctrl+Enter keeps its own clause below and works in every state, browse or not.
        if (key == AssistKey.Enter && modifiers == AssistModifiers.None)
        {
            return state.IsAcceptOnEnterArmed;
        }

        return key == AssistKey.Escape ||
               key == AssistKey.Up ||
               key == AssistKey.Down ||
               (isCtrl && !isShift && !isAlt && key == AssistKey.Enter) ||
               (isCtrl && isShift && key == AssistKey.P);
    }
}
