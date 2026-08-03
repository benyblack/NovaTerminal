using System;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Owns what the assist session is doing. Every change of state is a named transition; there is no
/// settable state property, so the set of ways the session can move is the set of methods here.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the mode field and the visibility/explicit-session bools that
/// <see cref="CommandAssistController"/> used to keep in sync by hand. The controller still writes
/// the view-model (visibility, popup, labels) itself; the state machine is the authority for the
/// three things behavior actually branches on: the current <see cref="Mode"/>, whether this is an
/// <see cref="IsExplicitSession"/>, and whether the current submission is suppressed.
/// </para>
/// <para>
/// <strong>Session state vs environment.</strong> A fact belongs here when it describes what the
/// assist surface is doing and changes as the user drives it. A fact belongs in
/// <see cref="AssistSessionContext"/> when it describes the terminal or shell the session happens
/// to be running in - alt-screen, remoteness, shell-integration configuration, observed OSC 133
/// markers, cwd, shell kind, ids. Those gate transitions from the outside (the controller refuses to
/// open anything while the alt screen is up) but they are not states of the session, and putting
/// them in the enum would multiply it by every combination of terminal condition.
/// </para>
/// </remarks>
internal sealed class AssistSessionStateMachine
{
    /// <summary>The current session state. Only the transitions below can change it.</summary>
    public AssistSessionState State { get; private set; } = AssistSessionState.Hidden;

    /// <summary>
    /// True when the text sitting in the shell's input line did not come from the user typing it
    /// here - today that means it was pasted - so neither history capture nor suggestion insertion
    /// may act on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept beside the enum rather than inside it on purpose: a paste can land in any state, so
    /// folding it in would double the state count while carrying no information about what the
    /// surface is showing. It is still only reachable through named transitions
    /// (<see cref="ObservePastedText"/> sets it; <see cref="ObserveTypedInput"/> and
    /// <see cref="CompleteSubmission"/> clear it), never through a setter.
    /// </para>
    /// <para>
    /// Phase 1c deleted the shadow query buffer and an earlier draft of this comment expected this
    /// flag to go with it. It does not, and the reason is worth stating: this is not a query fact.
    /// The grid reads pasted text as faithfully as typed text - what it cannot see is provenance,
    /// and provenance is the whole question. A pasted line must not be written to history as though
    /// the user composed it here, and must not have a suggestion spliced into it. Both of those
    /// survive the deletion, so this does too.
    /// </para>
    /// </remarks>
    public bool IsCurrentSubmissionSuppressed { get; private set; }

    /// <summary>The assist mode implied by the current state.</summary>
    public CommandAssistMode Mode => State switch
    {
        AssistSessionState.HistorySearch => CommandAssistMode.Search,
        AssistSessionState.Help => CommandAssistMode.Help,
        AssistSessionState.FixHint or AssistSessionState.FixPopup => CommandAssistMode.Fix,
        _ => CommandAssistMode.Suggest
    };

    /// <summary>
    /// True when the user opened this session deliberately. Widens the Suggest-mode scope to history
    /// and snippets, and keeps the bubble up even when a refresh returns nothing.
    /// </summary>
    public bool IsExplicitSession => State is
        AssistSessionState.ExplicitBubble or
        AssistSessionState.ExplicitPopup or
        AssistSessionState.HistorySearch;

    /// <summary>
    /// Whether the state ranks suggestions at all. Help and Fix render content produced elsewhere,
    /// so a refresh queued while they are up is dropped rather than allowed to overwrite them.
    /// </summary>
    public bool AllowsSuggestionRefresh => Mode is CommandAssistMode.Suggest or CommandAssistMode.Search;

    /// <summary>
    /// True when the surface on screen is one the user asked for by name, so no placement heuristic
    /// may hide it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wider than <see cref="IsExplicitSession"/> on purpose, and the difference is what the two
    /// questions are for. <see cref="IsExplicitSession"/> asks "may this session draw on history and
    /// snippets" - a ranking-scope question, so Help and Fix (which rank nothing) are not part of it.
    /// This asks "did the user ask to see something", which Help and a confident Fix popup plainly
    /// did.
    /// </para>
    /// <para>
    /// V2 Phase 3a consumers: <c>TerminalPane.ShouldSuppressConservativeRemoteAssist</c>, and the
    /// opacity half of the placement-correction stack. Both exist to avoid putting an
    /// <em>uninvited</em> overlay somewhere misleading on a pane whose prompt row is a guess; the
    /// answer for a surface the user summoned is different, because hiding it entirely is not a
    /// conservative outcome - it is the feature failing to appear (owner report: assist missing on one
    /// pane of an SSH split). Note that the correction passes themselves still run for these surfaces:
    /// what is bypassed is the hiding, not the correcting.
    /// </para>
    /// <para>
    /// <see cref="AssistSessionState.FixHint"/> is deliberately out. It is the bubble-only affordance
    /// for a diagnosis we are not confident about, i.e. the one Fix state the user did not ask for.
    /// </para>
    /// </remarks>
    public bool IsUserRequestedSurface => State is
        AssistSessionState.ExplicitBubble or
        AssistSessionState.ExplicitPopup or
        AssistSessionState.HistorySearch or
        AssistSessionState.Help or
        AssistSessionState.FixPopup;

    /// <summary>
    /// Whether an unmodified <c>Enter</c> belongs to Command Assist rather than to the shell.
    /// </summary>
    /// <param name="isPopupOpen">Whether the row list is on screen.</param>
    /// <param name="hasSelection">Whether one of those rows is selected.</param>
    /// <remarks>
    /// <para>
    /// <strong>The V2 Phase 3a keyboard change, and the reason it is a state predicate rather than a
    /// key list.</strong> Accept used to be <c>Ctrl+Enter</c> only, so a user who opened
    /// <c>Ctrl+R</c>, moved to a row and pressed <c>Enter</c> - which is what every shell's own
    /// reverse-search teaches - submitted their (empty) command line instead, and the submission reset
    /// dismissed the popup. Nothing was inserted and the surface vanished: the owner reported it as
    /// "lists history but does no action when I select".
    /// </para>
    /// <para>
    /// Ownership is therefore granted in exactly the state where <c>Enter</c> cannot mean anything
    /// else: the row list is open <em>and</em> a row is selected. In Suggest mode that state is only
    /// reachable by the user having moved the selection (<c>Up</c>/<c>Down</c> or a click - see
    /// <see cref="OpenPopupForSelection"/>, the only transition that opens a Suggest popup), and in
    /// Search mode it is reachable only because the user pressed <c>Ctrl+R</c>. Typing never reaches
    /// it: <see cref="ObserveTypedInput"/> closes the popup, so the ordinary
    /// type-a-command-and-press-Enter flow is untouched and <c>Enter</c> stays shell-owned there.
    /// </para>
    /// <para>
    /// Help and Fix are excluded even when their popup is open with a row selected. Their rows are
    /// documentation and diagnoses rather than a command line the user is composing, and a Fix popup
    /// in particular is on screen <em>after</em> a submission, where the next <c>Enter</c> is much
    /// more likely to be aimed at the shell. Both keep <c>Ctrl+Enter</c>.
    /// </para>
    /// </remarks>
    public bool AllowsAcceptOnEnter(bool isPopupOpen, bool hasSelection) =>
        isPopupOpen &&
        hasSelection &&
        State != AssistSessionState.Hidden &&
        Mode is CommandAssistMode.Suggest or CommandAssistMode.Search;

    /// <summary>
    /// Toggles the explicit assist session (the assist shortcut).
    /// </summary>
    /// <param name="isSurfaceVisible">
    /// Whether an assist surface is currently on screen. Passed in rather than derived because in
    /// the passive states visibility depends on whether the last refresh produced rows, which the
    /// view-model owns; the toggle has always keyed off what the user can see.
    /// </param>
    /// <returns>The state after the toggle.</returns>
    public AssistSessionState ToggleSession(bool isSurfaceVisible)
    {
        State = isSurfaceVisible ? AssistSessionState.Hidden : AssistSessionState.ExplicitBubble;
        return State;
    }

    /// <summary>Opens explicit history search (Ctrl+R).</summary>
    public void OpenSearch() => State = AssistSessionState.HistorySearch;

    /// <summary>Shows help content with the popup open.</summary>
    public void OpenHelp() => State = AssistSessionState.Help;

    /// <summary>
    /// Shows fix content: popup open for a confident diagnosis, bubble-only affordance otherwise.
    /// </summary>
    public void ShowFix(bool openPopup) =>
        State = openPopup ? AssistSessionState.FixPopup : AssistSessionState.FixHint;

    /// <summary>
    /// Enters one of the helper modes that render externally produced content.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown for <see cref="CommandAssistMode.Suggest"/> and <see cref="CommandAssistMode.Search"/>:
    /// those are reached by user intent through their own transitions, never by publishing content.
    /// </exception>
    public void EnterHelperMode(CommandAssistMode mode, bool openPopup)
    {
        switch (mode)
        {
            case CommandAssistMode.Help:
                OpenHelp();
                return;
            case CommandAssistMode.Fix:
                ShowFix(openPopup);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    mode,
                    "Only Help and Fix publish helper content; Suggest and Search are entered by user intent.");
        }
    }

    /// <summary>
    /// The user typed into the shell. Returns to Suggest mode with the popup closed, preserving
    /// whether this is an explicit session - typing on after a history search stays explicit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Known weakness, accepted rather than fixed:</strong> one keystroke after a paste
    /// clears <see cref="IsCurrentSubmissionSuppressed"/>, so "paste a command, add a character,
    /// press Enter" writes the pasted text to history as though the user had composed it. That was
    /// harmless in V1 because the shadow buffer only held the characters it had watched go by; it is
    /// not harmless now, because the grid reproduces pasted text perfectly and this flag is the only
    /// thing standing between a paste and the history file.
    /// </para>
    /// <para>
    /// The tightening - suppression survives until the submission resets it - was considered and
    /// deliberately not taken here. It is a behavior change, not a bug fix: paste-then-edit-then-run
    /// capturing is V1 behavior that
    /// <c>AssistSessionStateMachineTests.ObserveTypedInput_ClearsSubmissionSuppression</c> and
    /// <c>SubmissionSuppression_SurvivesEverythingExceptTypingAndSubmitting</c> both pin by name, and
    /// flipping it silently inside a phase whose subject is query truth would bury a policy decision
    /// about what belongs in history inside a refactor. It belongs with the Phase 3 capture-policy
    /// work, where the "paste a snippet and tweak it" case can be weighed against the "paste a
    /// credential-bearing one-liner" case in the open. Until then the loss is bounded by
    /// <c>ISecretsFilter</c> redaction and stated here rather than left to be rediscovered.
    /// </para>
    /// </remarks>
    public void ObserveTypedInput()
    {
        IsCurrentSubmissionSuppressed = false;
        State = IsExplicitSession ? AssistSessionState.ExplicitBubble : AssistSessionState.PassiveBubble;
    }

    /// <summary>
    /// The user pasted text. Drops back to a passive Suggest bubble and suppresses the current
    /// submission: the pasted text is not something the user composed here.
    /// </summary>
    public void ObservePastedText()
    {
        IsCurrentSubmissionSuppressed = true;
        State = AssistSessionState.PassiveBubble;
    }

    /// <summary>
    /// The user moved the selection, which opens the popup over whichever surface is up.
    /// </summary>
    /// <remarks>
    /// <see cref="AssistSessionState.Hidden"/> is a deliberate no-op: rows left over from a session
    /// that was toggled off can still be navigated, but there is no visible surface to browse, so
    /// the session does not re-open.
    /// </remarks>
    public void OpenPopupForSelection()
    {
        State = State switch
        {
            AssistSessionState.PassiveBubble => AssistSessionState.PassivePopup,
            AssistSessionState.ExplicitBubble => AssistSessionState.ExplicitPopup,
            AssistSessionState.FixHint => AssistSessionState.FixPopup,
            _ => State
        };
    }

    /// <summary>
    /// A Suggest-mode refresh landed, which closes the popup. Search, Help and Fix are untouched:
    /// their popups are not owned by the ranking pass.
    /// </summary>
    public void ClosePopupAfterRefresh()
    {
        State = State switch
        {
            AssistSessionState.PassivePopup => AssistSessionState.PassiveBubble,
            AssistSessionState.ExplicitPopup => AssistSessionState.ExplicitBubble,
            _ => State
        };
    }

    /// <summary>Dismisses the surface (Escape, or the host tearing the session down).</summary>
    public void Dismiss() => State = AssistSessionState.Hidden;

    /// <summary>The alt screen came up; the surface hides and the session ends.</summary>
    /// <remarks>
    /// Submission suppression deliberately survives: the alt screen says nothing about whether the
    /// text on the input line was typed or pasted.
    /// </remarks>
    public void HideForAltScreen() => State = AssistSessionState.Hidden;

    /// <summary>The user accepted a suggestion; the surface closes.</summary>
    public void AcceptSelection() => State = AssistSessionState.Hidden;

    /// <summary>
    /// The command line was submitted. Ends the session and clears submission suppression - the next
    /// line starts clean.
    /// </summary>
    public void CompleteSubmission()
    {
        IsCurrentSubmissionSuppressed = false;
        State = AssistSessionState.Hidden;
    }
}
