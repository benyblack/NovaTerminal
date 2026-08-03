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
/// the rule lives, and <see cref="CommandAssistController.IsAcceptOnEnterArmed"/>, which adds the two
/// facts the state machine cannot see: that the view-model is visible, and that the host's overlay is
/// actually rendered. The router takes the answer rather than recomputing it: the state machine, the
/// view-model and the host between them know whether the popup is open, whether a row is selected and
/// whether any of it is on screen, and a second implementation of that rule is a second thing to keep
/// in sync.
/// </param>
/// <param name="IsSelectionUpOwned">
/// Whether <c>Up</c> belongs to Command Assist rather than to the shell's history recall - see
/// <see cref="AssistSessionStateMachine.AllowsSelectionUp"/>. <c>Down</c> needs no equivalent: it is
/// assist-owned whenever a surface is visible, because "browse the suggestions" is the only thing it
/// can mean at a prompt.
/// </param>
public readonly record struct AssistKeyState(
    bool IsSurfaceVisible,
    bool IsAcceptOnEnterArmed,
    bool IsSelectionUpOwned);

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

        // Up is asymmetric with Down, and deliberately (PR #290 review). At a prompt with only a
        // passive bubble up, Up means "recall my last command" in every shell the user has ever used,
        // and taking it broke that: the assist consumed the key, opened its popup on the way through,
        // and the Enter that followed inserted a suggestion instead of submitting. Down is the
        // one-directional way into the list - it has no shell meaning at a prompt - and once the popup
        // is open, or the user summoned the surface by name, Up navigates as it always did.
        if (key == AssistKey.Up)
        {
            return state.IsSelectionUpOwned;
        }

        return key == AssistKey.Escape ||
               key == AssistKey.Down ||
               (isCtrl && !isShift && !isAlt && key == AssistKey.Enter) ||
               (isCtrl && isShift && key == AssistKey.P);
    }
}
