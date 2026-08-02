namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// What the assist session is doing right now - the single value that replaces the mode and
/// visibility bools <see cref="CommandAssistController"/> used to carry side by side.
/// </summary>
/// <remarks>
/// <para>
/// Two axes are folded into this enum because the code has always treated them as one thing:
/// the <see cref="Models.CommandAssistMode"/> the session is in, and whether the popup is open on
/// top of the bubble. A third axis - whether the user asked for the session explicitly - is folded
/// in as well: it survives a round trip through the popup and through
/// <see cref="AssistSessionStateMachine.ObserveTypedInput"/>, so it cannot be a transient flag.
/// </para>
/// <para>
/// Environment facts are deliberately <em>not</em> here. Alt-screen, remoteness, whether shell
/// integration is configured and whether its markers have been seen describe the terminal the
/// session runs in, not what the session is doing; they live in
/// <see cref="AssistSessionContext"/> and gate transitions from the outside.
/// </para>
/// </remarks>
public enum AssistSessionState
{
    /// <summary>No assist surface on screen. The resting state, and where every dismissal lands.</summary>
    Hidden,

    /// <summary>
    /// Suggest mode the user did not ask for: the bubble shows itself only while the last refresh
    /// produced rows. Path suggestions only - history and snippets stay out of an unasked-for hint.
    /// </summary>
    PassiveBubble,

    /// <summary>
    /// <see cref="PassiveBubble"/> with the popup opened by arrow-key navigation. Collapses back to
    /// <see cref="PassiveBubble"/> on the next Suggest-mode refresh, which closes the popup.
    /// </summary>
    PassivePopup,

    /// <summary>
    /// A session the user opened deliberately (assist toggle, or typing on after a history search).
    /// The bubble stays up even with no rows, and the suggestion scope widens to history + snippets.
    /// </summary>
    ExplicitBubble,

    /// <summary><see cref="ExplicitBubble"/> with the popup opened by arrow-key navigation.</summary>
    ExplicitPopup,

    /// <summary>
    /// Explicit history search (Ctrl+R): popup open, history-only scope, and the explicitness
    /// survives if the user keeps typing.
    /// </summary>
    HistorySearch,

    /// <summary>Help mode: popup open over docs and recipe rows. Not a suggestion-refreshing state.</summary>
    Help,

    /// <summary>
    /// Fix mode entered from a low-confidence failure analysis: bubble-only affordance, popup closed
    /// until the user navigates into it.
    /// </summary>
    FixHint,

    /// <summary>Fix mode entered from a high-confidence failure analysis: popup opened immediately.</summary>
    FixPopup
}
