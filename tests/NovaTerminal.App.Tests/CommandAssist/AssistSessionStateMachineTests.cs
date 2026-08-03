using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The full transition table. Every state gets every event, so a new state or a new transition
/// cannot be added without deciding - here, in one place - what it does from everywhere else.
/// </summary>
public sealed class AssistSessionStateMachineTests
{
    [Fact]
    public void NewMachine_StartsHiddenWithNothingSuppressed()
    {
        var machine = new AssistSessionStateMachine();

        Assert.Equal(AssistSessionState.Hidden, machine.State);
        Assert.False(machine.IsCurrentSubmissionSuppressed);
        Assert.False(machine.IsExplicitSession);
        Assert.Equal(CommandAssistMode.Suggest, machine.Mode);
    }

    /// <summary>Guards the table below: every state must be reachable by the documented route.</summary>
    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void CreateInState_ReachesTheRequestedState(AssistSessionState state)
    {
        Assert.Equal(state, CreateInState(state).State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden, CommandAssistMode.Suggest)]
    [InlineData(AssistSessionState.PassiveBubble, CommandAssistMode.Suggest)]
    [InlineData(AssistSessionState.PassivePopup, CommandAssistMode.Suggest)]
    [InlineData(AssistSessionState.ExplicitBubble, CommandAssistMode.Suggest)]
    [InlineData(AssistSessionState.ExplicitPopup, CommandAssistMode.Suggest)]
    [InlineData(AssistSessionState.HistorySearch, CommandAssistMode.Search)]
    [InlineData(AssistSessionState.Help, CommandAssistMode.Help)]
    [InlineData(AssistSessionState.FixHint, CommandAssistMode.Fix)]
    [InlineData(AssistSessionState.FixPopup, CommandAssistMode.Fix)]
    public void Mode_IsDerivedFromState(AssistSessionState state, CommandAssistMode expected)
    {
        Assert.Equal(expected, CreateInState(state).Mode);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden, false)]
    [InlineData(AssistSessionState.PassiveBubble, false)]
    [InlineData(AssistSessionState.PassivePopup, false)]
    [InlineData(AssistSessionState.ExplicitBubble, true)]
    [InlineData(AssistSessionState.ExplicitPopup, true)]
    [InlineData(AssistSessionState.HistorySearch, true)]
    [InlineData(AssistSessionState.Help, false)]
    [InlineData(AssistSessionState.FixHint, false)]
    [InlineData(AssistSessionState.FixPopup, false)]
    public void IsExplicitSession_IsDerivedFromState(AssistSessionState state, bool expected)
    {
        Assert.Equal(expected, CreateInState(state).IsExplicitSession);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden, true)]
    [InlineData(AssistSessionState.PassiveBubble, true)]
    [InlineData(AssistSessionState.PassivePopup, true)]
    [InlineData(AssistSessionState.ExplicitBubble, true)]
    [InlineData(AssistSessionState.ExplicitPopup, true)]
    [InlineData(AssistSessionState.HistorySearch, true)]
    [InlineData(AssistSessionState.Help, false)]
    [InlineData(AssistSessionState.FixHint, false)]
    [InlineData(AssistSessionState.FixPopup, false)]
    public void AllowsSuggestionRefresh_IsFalseForContentModes(AssistSessionState state, bool expected)
    {
        Assert.Equal(expected, CreateInState(state).AllowsSuggestionRefresh);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void ToggleSession_WhenSurfaceIsVisible_AlwaysHides(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.Equal(AssistSessionState.Hidden, machine.ToggleSession(isSurfaceVisible: true));
        Assert.Equal(AssistSessionState.Hidden, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void ToggleSession_WhenNoSurfaceIsVisible_OpensAnExplicitSession(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.Equal(AssistSessionState.ExplicitBubble, machine.ToggleSession(isSurfaceVisible: false));
        Assert.True(machine.IsExplicitSession);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void OpenSearch_FromAnyState_EntersHistorySearch(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.OpenSearch();

        Assert.Equal(AssistSessionState.HistorySearch, machine.State);
        Assert.True(machine.IsExplicitSession);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void OpenHelp_FromAnyState_EntersHelp(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.OpenHelp();

        Assert.Equal(AssistSessionState.Help, machine.State);
        Assert.False(machine.IsExplicitSession);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void ShowFix_FromAnyState_EntersFixWithTheRequestedPopupState(AssistSessionState state)
    {
        AssistSessionStateMachine confident = CreateInState(state);
        AssistSessionStateMachine tentative = CreateInState(state);

        confident.ShowFix(openPopup: true);
        tentative.ShowFix(openPopup: false);

        Assert.Equal(AssistSessionState.FixPopup, confident.State);
        Assert.Equal(AssistSessionState.FixHint, tentative.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassiveBubble, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.ExplicitBubble, AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup, AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.HistorySearch, AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.Help, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.FixHint, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.FixPopup, AssistSessionState.PassiveBubble)]
    public void ObserveTypedInput_ReturnsToSuggestPreservingExplicitness(
        AssistSessionState state,
        AssistSessionState expected)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.ObserveTypedInput();

        Assert.Equal(expected, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void ObservePastedText_DropsToAPassiveBubbleAndSuppressesTheSubmission(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.ObservePastedText();

        Assert.Equal(AssistSessionState.PassiveBubble, machine.State);
        Assert.True(machine.IsCurrentSubmissionSuppressed);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden, AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble, AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.PassivePopup, AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble, AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.ExplicitPopup, AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch, AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help, AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint, AssistSessionState.FixPopup)]
    [InlineData(AssistSessionState.FixPopup, AssistSessionState.FixPopup)]
    public void OpenPopupForSelection_OpensThePopupOverWhateverIsUp(
        AssistSessionState state,
        AssistSessionState expected)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.OpenPopupForSelection();

        Assert.Equal(expected, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden, AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup, AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.ExplicitBubble, AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup, AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.HistorySearch, AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help, AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint, AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup, AssistSessionState.FixPopup)]
    public void ClosePopupAfterRefresh_OnlyCollapsesSuggestPopups(
        AssistSessionState state,
        AssistSessionState expected)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.ClosePopupAfterRefresh();

        Assert.Equal(expected, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void Dismiss_FromAnyState_Hides(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.Dismiss();

        Assert.Equal(AssistSessionState.Hidden, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void HideForAltScreen_FromAnyState_Hides(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.HideForAltScreen();

        Assert.Equal(AssistSessionState.Hidden, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void AcceptSelection_FromAnyState_Hides(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        machine.AcceptSelection();

        Assert.Equal(AssistSessionState.Hidden, machine.State);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixHint)]
    [InlineData(AssistSessionState.FixPopup)]
    public void CompleteSubmission_FromAnyState_HidesAndClearsSuppression(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);
        machine.ObservePastedText();

        machine.CompleteSubmission();

        Assert.Equal(AssistSessionState.Hidden, machine.State);
        Assert.False(machine.IsCurrentSubmissionSuppressed);
    }

    [Theory]
    [InlineData(CommandAssistMode.Suggest)]
    [InlineData(CommandAssistMode.Search)]
    public void EnterHelperMode_ForANonContentMode_IsRejected(CommandAssistMode mode)
    {
        var machine = new AssistSessionStateMachine();

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.EnterHelperMode(mode, openPopup: true));
        Assert.Equal(AssistSessionState.Hidden, machine.State);
    }

    [Theory]
    [InlineData(CommandAssistMode.Help, true, AssistSessionState.Help)]
    [InlineData(CommandAssistMode.Help, false, AssistSessionState.Help)]
    [InlineData(CommandAssistMode.Fix, true, AssistSessionState.FixPopup)]
    [InlineData(CommandAssistMode.Fix, false, AssistSessionState.FixHint)]
    public void EnterHelperMode_ForAContentMode_EntersIt(
        CommandAssistMode mode,
        bool openPopup,
        AssistSessionState expected)
    {
        var machine = new AssistSessionStateMachine();

        machine.EnterHelperMode(mode, openPopup);

        Assert.Equal(expected, machine.State);
    }

    [Fact]
    public void ObserveTypedInput_ClearsSubmissionSuppression()
    {
        var machine = new AssistSessionStateMachine();
        machine.ObservePastedText();

        machine.ObserveTypedInput();

        Assert.False(machine.IsCurrentSubmissionSuppressed);
    }

    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    public void SubmissionSuppression_SurvivesEverythingExceptTypingAndSubmitting(AssistSessionState state)
    {
        var machine = new AssistSessionStateMachine();
        machine.ObservePastedText();

        MoveTo(machine, state);
        machine.Dismiss();
        machine.HideForAltScreen();
        machine.AcceptSelection();
        machine.OpenPopupForSelection();
        machine.ClosePopupAfterRefresh();

        Assert.True(machine.IsCurrentSubmissionSuppressed);
    }

    /// <summary>
    /// The one path that has to survive the split: Ctrl+R, then keep typing. The session stays
    /// explicit, which is what widens the suggestion scope back out to history and snippets.
    /// </summary>
    [Fact]
    public void HistorySearch_ThenTyping_StaysAnExplicitSession()
    {
        var machine = new AssistSessionStateMachine();

        machine.OpenSearch();
        machine.ObserveTypedInput();

        Assert.Equal(AssistSessionState.ExplicitBubble, machine.State);
        Assert.True(machine.IsExplicitSession);
        Assert.Equal(CommandAssistMode.Suggest, machine.Mode);
    }

    // -------------------------------------------------- accept on Enter (V2 Phase 3a)

    /// <summary>
    /// The states where an unmodified <c>Enter</c> inserts the selected row instead of submitting the
    /// command line: the two ranking modes, with the popup open and a row selected.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    public void AllowsAcceptOnEnter_WhenBrowsingARankedList_IsTrue(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.True(machine.AllowsAcceptOnEnter(isPopupOpen: true, hasSelection: true));
    }

    /// <summary>
    /// Help and Fix render content the user did not compose and Fix appears after a submission, so
    /// their <c>Enter</c> stays the shell's; both keep <c>Ctrl+Enter</c>.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixPopup)]
    [InlineData(AssistSessionState.FixHint)]
    public void AllowsAcceptOnEnter_InHelperModes_IsFalse(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.False(machine.AllowsAcceptOnEnter(isPopupOpen: true, hasSelection: true));
    }

    /// <summary>
    /// The typing flow, which must be untouched: a bubble is not a browse state whatever mode it is
    /// in, so Enter reaches the shell and submits.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    public void AllowsAcceptOnEnter_WithNoOpenPopup_IsFalse(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.False(machine.AllowsAcceptOnEnter(isPopupOpen: false, hasSelection: true));
    }

    [Fact]
    public void AllowsAcceptOnEnter_WithAnOpenPopupButNoSelectedRow_IsFalse()
    {
        AssistSessionStateMachine machine = CreateInState(AssistSessionState.HistorySearch);

        Assert.False(machine.AllowsAcceptOnEnter(isPopupOpen: true, hasSelection: false));
    }

    /// <summary>
    /// Rows left over from a session that was toggled off are still navigable (see
    /// <c>OpenPopupForSelection</c>), so the Hidden guard is not redundant with the popup flag.
    /// </summary>
    [Fact]
    public void AllowsAcceptOnEnter_WhenHidden_IsFalse()
    {
        AssistSessionStateMachine machine = CreateInState(AssistSessionState.Hidden);

        Assert.False(machine.AllowsAcceptOnEnter(isPopupOpen: true, hasSelection: true));
    }

    // ------------------------------------- Up belongs to the shell while typing (PR #290 review)

    /// <summary>
    /// The blocker, at the layer that decides it: in a passive bubble the user did not ask for,
    /// <c>Up</c> is the shell's history recall and Command Assist may not take it.
    /// </summary>
    /// <remarks>
    /// <c>FixHint</c> is in the table for the same reason it is out of
    /// <c>IsUserRequestedSurface</c>: it is a bubble-only affordance for a diagnosis nobody requested.
    /// </remarks>
    [Theory]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.FixHint)]
    public void AllowsSelectionUp_InAPassiveBubble_IsFalse(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.False(machine.AllowsSelectionUp(isPopupOpen: false));
    }

    /// <summary>
    /// Once the row list is open the user is demonstrably browsing it, so <c>Up</c> navigates - even in
    /// a passive session, which is the one the user reached with <c>Down</c>.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixPopup)]
    public void AllowsSelectionUp_WithTheListOpen_IsTrue(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.True(machine.AllowsSelectionUp(isPopupOpen: true));
    }

    /// <summary>
    /// A surface the user summoned by name owns both arrows from the first keypress: the list is what
    /// they asked for, so reaching it must not require a specific direction.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.HistorySearch)]
    public void AllowsSelectionUp_InASummonedSurfaceWithNoOpenList_IsStillTrue(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.True(machine.AllowsSelectionUp(isPopupOpen: false));
    }

    /// <summary>
    /// Hidden owns nothing, popup flag or not - the same guard <c>AllowsAcceptOnEnter</c> needs, for the
    /// same reason: leftover rows stay navigable objects while no surface is on screen.
    /// </summary>
    [Fact]
    public void AllowsSelectionUp_WhenHidden_IsFalse()
    {
        AssistSessionStateMachine machine = CreateInState(AssistSessionState.Hidden);

        Assert.False(machine.AllowsSelectionUp(isPopupOpen: true));
    }

    // ------------------------------------------- user-requested surfaces (V2 Phase 3a)

    /// <summary>
    /// The surfaces no placement heuristic may hide. Wider than <c>IsExplicitSession</c>: Help and a
    /// confident Fix popup were asked for even though they rank nothing.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.ExplicitBubble)]
    [InlineData(AssistSessionState.ExplicitPopup)]
    [InlineData(AssistSessionState.HistorySearch)]
    [InlineData(AssistSessionState.Help)]
    [InlineData(AssistSessionState.FixPopup)]
    public void IsUserRequestedSurface_ForSummonedSurfaces_IsTrue(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.True(machine.IsUserRequestedSurface);
    }

    /// <summary>
    /// The passive surfaces, which the conservative-placement stack still applies to - including the
    /// bubble-only Fix affordance, the one Fix state the user did not ask for.
    /// </summary>
    [Theory]
    [InlineData(AssistSessionState.Hidden)]
    [InlineData(AssistSessionState.PassiveBubble)]
    [InlineData(AssistSessionState.PassivePopup)]
    [InlineData(AssistSessionState.FixHint)]
    public void IsUserRequestedSurface_ForUninvitedSurfaces_IsFalse(AssistSessionState state)
    {
        AssistSessionStateMachine machine = CreateInState(state);

        Assert.False(machine.IsUserRequestedSurface);
    }

    private static AssistSessionStateMachine CreateInState(AssistSessionState state)
    {
        var machine = new AssistSessionStateMachine();
        MoveTo(machine, state);
        return machine;
    }

    private static void MoveTo(AssistSessionStateMachine machine, AssistSessionState state)
    {
        switch (state)
        {
            case AssistSessionState.Hidden:
                machine.Dismiss();
                return;
            case AssistSessionState.PassiveBubble:
                machine.Dismiss();
                machine.ObserveTypedInput();
                return;
            case AssistSessionState.PassivePopup:
                machine.Dismiss();
                machine.ObserveTypedInput();
                machine.OpenPopupForSelection();
                return;
            case AssistSessionState.ExplicitBubble:
                machine.Dismiss();
                machine.ToggleSession(isSurfaceVisible: false);
                return;
            case AssistSessionState.ExplicitPopup:
                machine.Dismiss();
                machine.ToggleSession(isSurfaceVisible: false);
                machine.OpenPopupForSelection();
                return;
            case AssistSessionState.HistorySearch:
                machine.OpenSearch();
                return;
            case AssistSessionState.Help:
                machine.OpenHelp();
                return;
            case AssistSessionState.FixHint:
                machine.ShowFix(openPopup: false);
                return;
            case AssistSessionState.FixPopup:
                machine.ShowFix(openPopup: true);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unhandled state in the test table.");
        }
    }
}
