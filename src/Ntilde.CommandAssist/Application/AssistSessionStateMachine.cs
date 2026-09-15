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

    /// <summary>
    /// True when the user dismissed an uninvited surface on this command line, so the passive typing
    /// bubble stays down until the line is submitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>V2 Phase 3b, task 1.</strong> The passive bubble appears without being asked for, so
    /// Escape has to mean more than "hide it once": without this, the very next keystroke queues a
    /// passive refresh and the bubble the user just dismissed comes straight back. The design doc's
    /// Pillar 4 says "Escape hides it for the rest of the command", and this is that scope - cleared
    /// by <see cref="CompleteSubmission"/> and by nothing else, so the next command line starts
    /// unsuppressed.
    /// </para>
    /// <para>
    /// <strong>Cleared by <see cref="BeginCommandLine"/> as well, since the PR #293 review.</strong>
    /// <see cref="CompleteSubmission"/> alone was not enough, because it only runs for a submission
    /// Command Assist saw - a local <c>Enter</c>. Every other way a command line ends left the flag set
    /// for the rest of the session: <c>Ctrl+C</c>, PSReadLine's own <c>Escape</c> (which clears the line
    /// without submitting anything), a pasted line ending in a newline, a broadcast-to-all-panes send, an
    /// agent-sent command. Two Escapes in a row was enough to reach it - the first took the bubble down
    /// and set the flag, the second fell through to the shell, which cleared the line - and from there the
    /// passive bubble never came back for the life of the pane. Anchoring the clear to <c>OSC 133;B</c>
    /// instead ties it to the thing it is actually scoped to: a new command line.
    /// </para>
    /// <para>
    /// Beside the enum rather than in it, for the same reason as
    /// <see cref="IsCurrentSubmissionSuppressed"/>: it is orthogonal to what the surface is showing
    /// (a user can dismiss, then summon <c>Ctrl+R</c>, then be back in a suppressed passive state
    /// afterwards), and folding it into the enum would double the state count.
    /// </para>
    /// <para>
    /// It does not suppress <em>anything the user asks for</em>. <see cref="AllowsPassiveSuggestions"/>
    /// is the only consumer and it exempts explicit sessions, so <c>Ctrl+Space</c>, <c>Ctrl+R</c>,
    /// Help and Fix all still open after an Escape - which is what makes a per-command scope safe.
    /// Distinguishing the two is why <see cref="DismissForCurrentCommand"/> exists separately from
    /// <see cref="Dismiss"/>: the host tearing a session down, and an accept closing the surface, are
    /// not the user saying "not on this line".
    /// </para>
    /// </remarks>
    public bool IsPassiveSurfaceSuppressed { get; private set; }

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
    /// Whether a refresh the user did not ask for may run at all. False for the rest of a command
    /// line the user pressed Escape on; see <see cref="IsPassiveSurfaceSuppressed"/>.
    /// </summary>
    public bool AllowsPassiveSuggestions => IsExplicitSession || !IsPassiveSurfaceSuppressed;

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
    /// it from the passive flow: <see cref="ObserveTypedInput"/> closes the popup, so the ordinary
    /// type-a-command-and-press-Enter flow is untouched and <c>Enter</c> stays shell-owned there.
    /// </para>
    /// <para>
    /// <strong>Typing inside history search keeps it armed, and that is the point.</strong> Since
    /// typing no longer leaves <see cref="AssistSessionState.HistorySearch"/>, the popup and its
    /// selection survive a filter keystroke, so <c>Enter</c> goes on inserting the highlighted row
    /// while the user narrows the list. That is what <c>Ctrl+R</c> means everywhere else - the match
    /// is what <c>Enter</c> takes - and it is the state the user is demonstrably in, having summoned a
    /// list of history entries and then described which one. <c>Escape</c> is the way back to a
    /// shell-owned <c>Enter</c>, exactly as it is for the row list opened by <c>Ctrl+R</c> alone.
    /// </para>
    /// <para>
    /// Help and Fix are excluded even when their popup is open with a row selected. Their rows are
    /// documentation and diagnoses rather than a command line the user is composing, and a Fix popup
    /// in particular is on screen <em>after</em> a submission, where the next <c>Enter</c> is much
    /// more likely to be aimed at the shell. Both keep <c>Ctrl+Enter</c>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether <c>Up</c> belongs to Command Assist rather than to the shell's history recall.
    /// </summary>
    /// <param name="isPopupOpen">Whether the row list is on screen.</param>
    /// <remarks>
    /// <para>
    /// <strong>The PR #290 review fix, and the keyboard model it settles.</strong> Phase 3a shipped
    /// <c>Up</c> and <c>Down</c> both owned whenever any surface was visible, which turned the passive
    /// typing bubble into a trap: type <c>git st</c>, press <c>Up</c> expecting the shell's history,
    /// and instead the assist ate the key, <c>MoveSelectionUp</c>'s clamp-to-zero opened the popup, and
    /// the next <c>Enter</c> inserted a suggestion rather than running the command. Two keys were
    /// silently redefined by a surface the user never asked for.
    /// </para>
    /// <para>
    /// So the entry into the list is one-directional in the passive states: <c>Down</c> browses
    /// suggestions (it has no meaning at a shell prompt, so nothing is taken), <c>Up</c> stays the
    /// shell's history recall. This is what fish and PSReadLine teach. Once the popup is open the user
    /// is demonstrably in the list and <c>Up</c> navigates it; and in a surface the user summoned by
    /// name (<c>Ctrl+Space</c>, <c>Ctrl+R</c>, Help, a confident Fix popup - see
    /// <see cref="IsUserRequestedSurface"/>) both arrows are owned from the first keypress, because
    /// there the list <em>is</em> what the user asked for.
    /// </para>
    /// <para>
    /// <see cref="AssistSessionState.FixHint"/> falls out on the passive side, which is right: it is
    /// the one Fix state the user did not ask for, and <c>Down</c> still opens its popup.
    /// </para>
    /// </remarks>
    public bool AllowsSelectionUp(bool isPopupOpen) =>
        State != AssistSessionState.Hidden &&
        (isPopupOpen || IsUserRequestedSurface);

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
    /// whether this is an explicit session - except in <see cref="AssistSessionState.HistorySearch"/>,
    /// where typing is the filter and the session stays where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>History search keeps itself, and this is a policy reversal.</strong> Phase 0c had this
    /// transition drop <see cref="AssistSessionState.HistorySearch"/> to
    /// <see cref="AssistSessionState.ExplicitBubble"/>, on the reading that typing is a return to the
    /// Suggest scope with the explicitness carried along - <c>ObserveTypedInput_ReturnsToSuggestPreservingExplicitness</c>
    /// pinned exactly that. What it meant in the user's hands was that <c>Ctrl+R</c> followed by a
    /// single character closed the popup: the owner's report is "when Ctrl+R shows history, if I type
    /// it just hides that instead of searching". Every shell's reverse-search and every fuzzy finder
    /// does the opposite - the list stays up and narrows - and there is nothing else the keystroke
    /// could plausibly have meant, because the user summoned a list of history entries and then began
    /// describing which one.
    /// </para>
    /// <para>
    /// The explicitness the old transition was protecting is not lost, it is stronger: staying in
    /// <see cref="AssistSessionState.HistorySearch"/> is still an <see cref="IsExplicitSession"/> and
    /// still an <see cref="IsUserRequestedSurface"/>, so everything the old ExplicitBubble bought
    /// (history and snippets in scope, a surface no placement heuristic may hide, immunity from the
    /// per-command Escape suppression) holds, and the mode the user asked for holds too.
    /// </para>
    /// <para>
    /// Nothing about where the keystroke <em>goes</em> changes: the character is not search-box input,
    /// it reaches the shell like any other, and the query the refreshed ranking reads is the command
    /// line the shell painted (<see cref="AssistQuerySnapshot.TextBeforeCursor"/>). Backspace arrives
    /// here as the same event and narrows the query back the same way. The passive flow and the
    /// helper states are untouched: typing out of Help or Fix still drops to a passive bubble, which
    /// is right, because there the keystroke is the user moving on from content they asked to read
    /// rather than refining it.
    /// </para>
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

        // Typing in history search is the search. Written as an early return rather than folded into
        // the ternary because IsExplicitSession is true here too, so the ternary cannot express it.
        if (State == AssistSessionState.HistorySearch)
        {
            return;
        }

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

    /// <summary>Dismisses the surface (the host tearing the session down, or an accept closing it).</summary>
    public void Dismiss() => State = AssistSessionState.Hidden;

    /// <summary>
    /// Escape's first stage in the passive flow: closes an uninvited popup and leaves the bubble it
    /// grew out of on screen. Returns <see langword="false"/> - changing nothing - in every other
    /// state, which is the caller's signal to fall through to <see cref="DismissForCurrentCommand"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Dogfood round 4, item 3.</strong> The passive flow reaches its popup by one keystroke -
    /// <c>Down</c> on a bubble the user did not ask for - and Escape used to undo far more than that
    /// one keystroke: it hid everything <em>and</em> took
    /// <see cref="IsPassiveSurfaceSuppressed"/>, so the suggestion was gone for the rest of the command
    /// line. The owner reported it as "Esc kills everything". A key that enters a surface and a key
    /// that leaves it should be inverses, so <c>Down</c> now has one.
    /// </para>
    /// <para>
    /// <strong><see cref="AssistSessionState.PassivePopup"/> only, deliberately.</strong> The other
    /// popups are all surfaces the user summoned by name - <c>Ctrl+Space</c>, <c>Ctrl+R</c>, Help, a
    /// confident Fix - and for those Escape means "I am done with the thing I asked for", so closing
    /// outright is the answer they already give and the one every other list in the product gives.
    /// Staging them would cost a second keypress to close something the user opened deliberately and
    /// would make Escape's meaning depend on which surface happened to be up.
    /// <see cref="AssistSessionState.FixPopup"/> is out for the same reason even though
    /// <see cref="AssistSessionState.FixHint"/> can reach it: a Fix popup is on screen after a command
    /// failed, where the user's next act is far more likely to be "clear this" than "show me less of
    /// it".
    /// </para>
    /// <para>
    /// Nothing else is touched. In particular this does <em>not</em> set
    /// <see cref="IsPassiveSurfaceSuppressed"/> - the whole point is that the suggestion survives - and
    /// it does not clear the selection, so the bubble goes on describing the row the user browsed to
    /// and the insert chord goes on inserting that same row.
    /// </para>
    /// </remarks>
    public bool TryCollapsePopupToBubble()
    {
        if (State != AssistSessionState.PassivePopup)
        {
            return false;
        }

        State = AssistSessionState.PassiveBubble;
        return true;
    }

    /// <summary>
    /// The user dismissed the surface with Escape: hide it, and keep the passive bubble down for the
    /// rest of this command line.
    /// </summary>
    /// <remarks>
    /// Note what this does <em>not</em> clear: <see cref="IsCurrentSubmissionSuppressed"/>. Escaping a
    /// bubble says nothing about whether the text on the line was pasted, and conflating the two would
    /// let "paste, Escape, Enter" write a pasted line to history.
    /// </remarks>
    public void DismissForCurrentCommand()
    {
        IsPassiveSurfaceSuppressed = true;
        State = AssistSessionState.Hidden;
    }

    /// <summary>
    /// The shell printed a prompt and handed its line editor to the user (<c>OSC 133;B</c>): a new
    /// command line has begun, so the per-command Escape suppression is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PR #293 review, blocker 2.</strong> <see cref="IsPassiveSurfaceSuppressed"/> is scoped to
    /// "the rest of this command line", and before this the only thing that ended a command line was
    /// <see cref="CompleteSubmission"/> - i.e. a local <c>Enter</c> that Command Assist observed. Any
    /// other ending (see that flag's remarks) left the suppression in force indefinitely. <c>B</c> is the
    /// marker that means "here is a fresh line", so it is the honest place to reset.
    /// </para>
    /// <para>
    /// Deliberately narrow. It does <em>not</em> touch <see cref="IsCurrentSubmissionSuppressed"/>: a new
    /// prompt says nothing about whether the text about to appear on it was typed or pasted, and pasting
    /// happens after the prompt is printed. It does not touch <see cref="State"/> either - the surface the
    /// user has up (a <c>Ctrl+R</c> popup that survived a repaint) is not the suppression flag's business.
    /// </para>
    /// <para>
    /// <strong>Accepted caveat: <c>B</c> is not guaranteed to be once per command line.</strong> Our
    /// <c>B</c> is appended to the prompt string, so anything that reprints the prompt re-emits it -
    /// <c>Ctrl+L</c>, and whichever render paths a given PSReadLine version routes through
    /// <c>InvokePrompt</c>. A repaint mid-line therefore un-suppresses a bubble the user dismissed, and
    /// the next keystroke brings it back. (Measured on PSReadLine 2.3 in a live pane: a window resize
    /// repaints the input only and does <em>not</em> re-emit <c>B</c>, so that path does not un-suppress.
    /// The set is version-dependent, which is why this is stated as a caveat rather than a list.) It is
    /// the wrong behavior in a narrow case and strictly better than the bug it replaces: a suppression
    /// that never clears is silent and permanent, where this one costs the user a second <c>Escape</c>
    /// after they redrew their screen. Tightening it would need a "same logical line" identity the marks
    /// do not carry.
    /// </para>
    /// </remarks>
    public void BeginCommandLine()
    {
        IsPassiveSurfaceSuppressed = false;
    }

    /// <summary>The alt screen came up; the surface hides and the session ends.</summary>
    /// <remarks>
    /// Submission suppression deliberately survives: the alt screen says nothing about whether the
    /// text on the input line was typed or pasted.
    /// </remarks>
    public void HideForAltScreen() => State = AssistSessionState.Hidden;

    /// <summary>The user accepted a suggestion; the surface closes.</summary>
    public void AcceptSelection() => State = AssistSessionState.Hidden;

    /// <summary>
    /// The command line was submitted. Ends the session and clears both suppressions - the next line
    /// starts clean.
    /// </summary>
    public void CompleteSubmission()
    {
        IsCurrentSubmissionSuppressed = false;
        IsPassiveSurfaceSuppressed = false;
        State = AssistSessionState.Hidden;
    }
}
