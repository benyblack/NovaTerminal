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
/// <para>
/// <strong>Popup-openness is not read off this enum.</strong> The state names say which states
/// <em>intend</em> the popup to be up, but the authority every code path actually consults is
/// <see cref="ViewModels.CommandAssistBarViewModel.IsPopupOpen"/> - the controller writes it, and
/// <c>SetSelectedIndex</c> / <c>ApplyRefreshOutcome</c> branch on it. The two can disagree (a
/// pre-existing latent bug: nothing keeps them in sync), so deriving a second predicate here would
/// only offer a plausible-looking answer that production does not use. Resolve the view-model sync
/// first; until then, treat the popup axis of these names as documentation, not as a source.
/// </para>
/// <para>
/// <strong>Public only for the transition table.</strong> Every member that traffics in this enum -
/// <see cref="AssistSessionStateMachine"/> and <c>CommandAssistController.SessionState</c> - is
/// <c>internal</c>, so nothing outside this assembly can reach a value of it in production. It stays
/// public because <c>AssistSessionStateMachineTests</c> drives the whole transition table through
/// <c>[Theory]</c> parameters of this type, and xUnit requires public test classes (xUnit1000),
/// which in turn requires their parameter types to be at least as accessible.
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
