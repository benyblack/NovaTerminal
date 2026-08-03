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
/// One in-surface Command Assist chord: the key, and the modifiers that must be held exactly.
/// </summary>
/// <remarks>
/// Exact modifier equality, not "at least these". That was already the rule for accept-on-Enter
/// (Shift+Enter is a newline in several line editors; every modified Enter is a distinct CSI u
/// sequence under the kitty disambiguate tier) and V2 Phase 3b extends it to the rest: a
/// <c>Ctrl+Down</c> or an <c>Alt+Up</c> is something the shell's line editor may well act on, and the
/// router used to swallow both because it tested the key and ignored the modifiers.
/// </remarks>
public readonly record struct AssistKeyBinding(AssistKey Key, AssistModifiers Modifiers)
{
    /// <summary>Whether a keystroke is this chord.</summary>
    /// <remarks>
    /// <see cref="AssistKey.None"/> never matches. It is what the App boundary maps every key Command
    /// Assist does not model to, so a binding that ended up as <c>None</c> - a rebind to a key this
    /// enum does not carry - must match nothing rather than everything.
    /// </remarks>
    public bool Matches(AssistKey key, AssistModifiers modifiers) =>
        Key != AssistKey.None && key == Key && modifiers == Modifiers;
}

/// <summary>
/// The five in-surface chords, as resolved from the App's shortcut catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>V2 Phase 3b, task 2.</strong> These were hard-coded in the router, which meant the one
/// part of the Command Assist keyboard the user meets constantly was the one part they could not
/// rebind - and it also meant the hint strip's key names could not be anything but literals. They are
/// catalogue entries now (<c>command_assist_dismiss</c>, <c>_selection_up</c>, <c>_selection_down</c>,
/// <c>_accept</c>, <c>_insert</c> under <c>ShortcutScope.CommandAssist</c>), resolved App-side and
/// passed in here.
/// </para>
/// <para>
/// The defaults are exactly the shipped chords, so a user with no overrides - which is every user
/// until they open Settings - sees no change at all.
/// </para>
/// </remarks>
public sealed record AssistKeyBindings(
    AssistKeyBinding Dismiss,
    AssistKeyBinding SelectionUp,
    AssistKeyBinding SelectionDown,
    AssistKeyBinding Accept,
    AssistKeyBinding Insert)
{
    /// <summary>The shipped keyboard: <c>Esc</c>, <c>Up</c>, <c>Down</c>, <c>Enter</c>, <c>Ctrl+Enter</c>.</summary>
    public static AssistKeyBindings Default { get; } = new(
        Dismiss: new AssistKeyBinding(AssistKey.Escape, AssistModifiers.None),
        SelectionUp: new AssistKeyBinding(AssistKey.Up, AssistModifiers.None),
        SelectionDown: new AssistKeyBinding(AssistKey.Down, AssistModifiers.None),
        Accept: new AssistKeyBinding(AssistKey.Enter, AssistModifiers.None),
        Insert: new AssistKeyBinding(AssistKey.Enter, AssistModifiers.Control));
}

/// <summary>
/// What Command Assist does with a keystroke it owns.
/// </summary>
/// <remarks>
/// Returned instead of a bool so the host acts on the resolved <em>action</em> rather than
/// re-deciding from the key. <c>TerminalPane</c> used to ask "is this ours?" and then run its own
/// second cascade of key comparisons to find out which of ours it was; with rebindable chords that
/// second cascade would have been a second, divergent binding table.
/// </remarks>
public enum AssistKeyAction
{
    /// <summary>Not Command Assist's key; the terminal gets it.</summary>
    None = 0,
    Dismiss,
    SelectionUp,
    SelectionDown,

    /// <summary>Insert the selected row, in the browse state where the accept key is armed.</summary>
    Accept,

    /// <summary>Insert the selected row, in any state.</summary>
    Insert,
}

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
    /// <summary>
    /// Resolves a keystroke to the Command Assist action it triggers, or
    /// <see cref="AssistKeyAction.None"/>.
    /// </summary>
    public static AssistKeyAction Resolve(
        AssistKeyState state,
        AssistKey key,
        AssistModifiers modifiers,
        AssistKeyBindings? bindings = null)
    {
        if (!state.IsSurfaceVisible)
        {
            return AssistKeyAction.None;
        }

        AssistKeyBindings effective = bindings ?? AssistKeyBindings.Default;

        // Insert first, and this ordering is load-bearing rather than incidental. Accept and insert
        // are the same key with different modifiers by default, and the accept clause is conditional
        // on the browse state: testing accept first would let an unarmed Ctrl+Enter fall past both
        // when Accept and Insert resolve to overlapping chords.
        if (effective.Insert.Matches(key, modifiers))
        {
            return AssistKeyAction.Insert;
        }

        // The accept key, and only while browsing (V2 Phase 3a). Outside the browse state it belongs
        // to the shell - which for the default Enter is the ordinary
        // type-a-command-and-press-Enter flow, untouched.
        if (effective.Accept.Matches(key, modifiers))
        {
            return state.IsAcceptOnEnterArmed ? AssistKeyAction.Accept : AssistKeyAction.None;
        }

        // Up is asymmetric with Down, and deliberately (PR #290 review). At a prompt with only a
        // passive bubble up, Up means "recall my last command" in every shell the user has ever used,
        // and taking it broke that: the assist consumed the key, opened its popup on the way through,
        // and the Enter that followed inserted a suggestion instead of submitting. Down is the
        // one-directional way into the list - it has no shell meaning at a prompt - and once the popup
        // is open, or the user summoned the surface by name, Up navigates as it always did.
        if (effective.SelectionUp.Matches(key, modifiers))
        {
            return state.IsSelectionUpOwned ? AssistKeyAction.SelectionUp : AssistKeyAction.None;
        }

        if (effective.SelectionDown.Matches(key, modifiers))
        {
            return AssistKeyAction.SelectionDown;
        }

        if (effective.Dismiss.Matches(key, modifiers))
        {
            return AssistKeyAction.Dismiss;
        }

        // Pin/unpin is deliberately absent. It used to be a clause here on Ctrl+Shift+P, which is the
        // command palette's chord: MainWindow tried the pin first and fell through to the palette, so
        // whether Ctrl+Shift+P opened the palette depended on whether an assist row happened to be
        // selected. V2 Phase 3b gave pin its own catalogue entry (command_assist_pin), and because
        // that entry is dispatched from the window's shortcut handler it can be bound to the whole key
        // space rather than to the handful of keys AssistKey models.
        return AssistKeyAction.None;
    }

    /// <summary>Whether Command Assist owns this keystroke.</summary>
    public static bool IsAssistOwnedKey(
        AssistKeyState state,
        AssistKey key,
        AssistModifiers modifiers,
        AssistKeyBindings? bindings = null) =>
        Resolve(state, key, modifiers, bindings) != AssistKeyAction.None;
}
