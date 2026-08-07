using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Providers;
using NovaTerminal.CommandAssist.Providers.Local;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ViewModels;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// The facade the terminal pane talks to. It owns the assist view-model and the selected-row list,
/// and delegates everything else to three collaborators:
/// <see cref="AssistSessionStateMachine"/> (what the session is doing),
/// <see cref="CapturePipeline"/> (turning commands into history) and
/// <see cref="SuggestionOrchestrator"/> (producing ranked rows).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AssistSessionContext"/> carries the environment all three share. The controller keeps
/// the view-model writes because presentation is the facade's job: the state machine says the
/// session is in an explicit Suggest session, the controller decides that means the bubble is up and
/// the popup is not.
/// </para>
/// <para>
/// <strong>Query state (Phase 1c).</strong> The controller does not hold a query. It has no
/// <c>HandleTextInput(text)</c>, no backspace mirror and no paste mirror, because it has no buffer
/// for them to write into. The question "what is on the command line" is answered by reading the
/// terminal grid between the newest <c>OSC 133;B</c> mark and the cursor - see
/// <see cref="AssistQuerySnapshot"/> - and every consumer of the query (ranking, the help token, the
/// insertion planner, the view-model's <c>QueryText</c>) goes to that one source. Keystrokes remain
/// as <em>triggers</em>: <see cref="NotifyInputActivity"/> says "the line probably changed, look
/// again", and the looking is the orchestrator's job.
/// </para>
/// <para>
/// What survives from the old shadow buffer is exactly one bit:
/// <see cref="AssistSessionStateMachine.IsCurrentSubmissionSuppressed"/>. Paste suppression is a
/// statement about provenance ("the user did not compose this here"), which the grid cannot
/// reconstruct from pixels, and it gates history capture and insertion rather than the query.
/// </para>
/// </remarks>
public sealed class CommandAssistController
{
    private readonly AssistSessionStateMachine _state = new();
    private readonly AssistSessionContext _context = new();
    private readonly CapturePipeline _capturePipeline;
    private readonly SuggestionOrchestrator _suggestionOrchestrator;
    private readonly List<AssistSuggestion> _suggestions = new();
    private readonly Action<Action> _dispatch;

    /// <summary>
    /// The AI content-provider seam (V2 Phase 5). Every Help row and every Fix row on the surface
    /// came through here, including the ones the two local heuristics produce.
    /// </summary>
    /// <remarks>
    /// The controller no longer holds an <c>ICommandDocsProvider</c>, an <c>IRecipeProvider</c> or an
    /// <c>IErrorInsightService</c>: it holds providers. That is the whole point of the phase - the
    /// path a future AI provider would travel is the path the local heuristics travel today, so it is
    /// exercised on every Help and every Fix rather than on the day a remote provider first ships.
    /// </remarks>
    private readonly AssistContentProviderRegistry _contentProviders;

    /// <summary>
    /// The one place raw session text becomes text a provider may see. See
    /// <see cref="AssistContentRequestFactory"/> and <see cref="RedactedText"/>.
    /// </summary>
    private readonly AssistContentRequestFactory _requestFactory;

    private readonly CommandAssistModeRouter _modeRouter;
    private readonly CommandAssistResultBuilder _resultBuilder;
    private readonly Func<bool> _renderedSurfaceProbe;

    /// <summary>
    /// Whether an accept against the command line the surface was last built for would insert rather
    /// than refuse. Feeds the hint strip only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PR #293 review, blocker 1's follow-on.</strong> With PSReadLine's inline prediction
    /// showing, the cursor is never at the end of the painted line, so
    /// <see cref="AssistQuerySnapshot.IsUsableAsTypedPrefix"/> is false and
    /// <see cref="CommandAssistInsertionPlanner"/> refuses every accept. A hint strip that goes on
    /// advertising "Ctrl+Enter insert" in that state is promising a key that does nothing, which is the
    /// exact failure mode the strip's own comments call worse than no strip at all.
    /// </para>
    /// <para>
    /// A cached fact rather than a live probe, deliberately. The strip is republished from
    /// <see cref="CommandAssistBarViewModel.SyncPresentationState"/>, which runs on every view-model
    /// write - several per keystroke - and a live read would put a buffer-locking grid walk on the
    /// caller's thread each time, which is precisely the synchronous work the keystroke-latency tripwire
    /// exists to keep out. It is written where the surface's content is: from the ranking outcome
    /// (which read the snapshot anyway) and from <see cref="ApplyHelperSuggestions"/>. The cost is that
    /// it can lag the line by one pass; the strip under-promising for 75 ms is not a bug worth a grid
    /// read per property set.
    /// </para>
    /// <para>
    /// Starts <see langword="true"/> and stays true for a session with no grid to read - see
    /// <see cref="SuggestionRefreshOutcome.InsertionAppearsAvailable"/> for why degraded mode is not the
    /// same question.
    /// </para>
    /// </remarks>
    private bool _isInsertionAvailable = true;

    /// <summary>
    /// The clock the relative-time captions are rendered against. Overridable so the buckets in
    /// <see cref="AssistRelativeTime"/> can be tested without waiting for time to pass.
    /// </summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public CommandAssistController(
        IHistoryStore historyStore,
        ISecretsFilter secretsFilter,
        ISuggestionEngine suggestionEngine,
        ISnippetStore? snippetStore,
        ICommandDocsProvider? commandDocsProvider,
        IRecipeProvider? recipeProvider,
        IErrorInsightService? errorInsightService,
        CommandAssistModeRouter? modeRouter,
        CommandAssistResultBuilder? resultBuilder,
        Func<AssistQuerySnapshot?>? queryProvider = null,
        Func<bool>? renderedSurfaceProbe = null,
        Action<Action>? dispatch = null,
        TimeSpan? passiveRefreshDebounce = null,
        Func<TimeSpan, CancellationToken, Task>? refreshDelay = null,
        AssistContentProviderRegistry? contentProviders = null)
    {
        HistoryStore = historyStore;
        SecretsFilter = secretsFilter;
        SuggestionEngine = suggestionEngine;
        SnippetStore = snippetStore;
        _requestFactory = new AssistContentRequestFactory(secretsFilter);
        _contentProviders = contentProviders ??
            BuildLocalProviderRegistry(commandDocsProvider, recipeProvider, errorInsightService);
        _modeRouter = modeRouter ?? new CommandAssistModeRouter();
        _resultBuilder = resultBuilder ?? new CommandAssistResultBuilder();

        // No probe means "there is no host hiding anything", which is the honest answer for a
        // controller with no view attached (tests, the MCP surface): the only thing that can contradict
        // the view-model's visibility is a host that renders it, and there is none.
        _renderedSurfaceProbe = renderedSurfaceProbe ?? (() => true);
        ViewModel = new CommandAssistBarViewModel
        {
            // The hint strip and the key router must agree about what Enter does, so both read the
            // same predicate rather than each deciding for itself. Same for Up, which the strip used to
            // advertise unconditionally and the passive bubble no longer owns.
            AcceptOnEnterProbe = () => IsAcceptOnEnterArmed,
            SelectionUpOwnedProbe = () => IsSelectionUpOwned,
            InsertionAvailableProbe = () => _isInsertionAvailable
        };
        _dispatch = dispatch ?? (action => action());
        _capturePipeline = new CapturePipeline(historyStore, secretsFilter, _context);
        _suggestionOrchestrator = new SuggestionOrchestrator(
            historyStore,
            snippetStore,
            suggestionEngine,
            _context,
            // No provider means no grid, which is the same situation as a shell that emits no
            // marks: the session runs in degraded mode. Defaulting to "no truth available" rather
            // than throwing keeps every non-App host (tests, the MCP surface) on the honest path.
            queryProvider ?? (() => null),
            _dispatch,
            ApplyRefreshOutcome,
            passiveRefreshDebounce,
            refreshDelay);
    }

    /// <summary>
    /// Wraps whichever local services the host supplied as content providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three legacy service interfaces stay on the constructor rather than being replaced by a
    /// registry parameter, so that every existing caller - the pane, twenty-odd test helpers - keeps
    /// compiling and keeps meaning the same thing. A host that wants to compose the seam itself
    /// (which is what the AI milestone will do, at <c>CommandAssistServices</c>) passes
    /// <c>contentProviders</c> and these are ignored.
    /// </para>
    /// <para>
    /// <strong>A missing service registers no provider, rather than an empty one.</strong> The three
    /// <c>Empty*Provider</c> stubs this replaced turned "nothing is configured" into "we looked and
    /// found nothing", which are different sentences to a user - see <see cref="AssistEmptyStates"/>.
    /// In the shipped app both are always present; the distinction is reachable for a host that
    /// composes the controller without them.
    /// </para>
    /// </remarks>
    private static AssistContentProviderRegistry BuildLocalProviderRegistry(
        ICommandDocsProvider? commandDocsProvider,
        IRecipeProvider? recipeProvider,
        IErrorInsightService? errorInsightService)
    {
        var providers = new List<IAssistContentProvider>(2);

        if (commandDocsProvider != null || recipeProvider != null)
        {
            providers.Add(new LocalCommandKnowledgeProvider(commandDocsProvider, recipeProvider));
        }

        if (errorInsightService != null)
        {
            providers.Add(new LocalErrorInsightProvider(errorInsightService));
        }

        return new AssistContentProviderRegistry(providers);
    }

    public IHistoryStore HistoryStore { get; }
    public ISnippetStore? SnippetStore { get; }
    public ISecretsFilter SecretsFilter { get; }
    public ISuggestionEngine SuggestionEngine { get; }
    public CommandAssistBarViewModel ViewModel { get; }
    public IReadOnlyList<AssistSuggestion> Suggestions => _suggestions;

    /// <summary>What the session is doing right now. Exposed for diagnostics and tests.</summary>
    internal AssistSessionState SessionState => _state.State;

    /// <summary>
    /// Whether an unmodified <c>Enter</c> currently means "insert the selected row" rather than
    /// "submit the command line". See <see cref="AssistSessionStateMachine.AllowsAcceptOnEnter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single source of truth for the V2 Phase 3a keyboard change: the App's key interceptor puts
    /// this into <see cref="AssistKeyState"/>, and the bubble's hint strip renders it. Both are asking
    /// the same question of the same object.
    /// </para>
    /// <para>
    /// <strong>Three terms, and the third is the PR #290 review fix.</strong>
    /// <see cref="CommandAssistBarViewModel.IsVisible"/> is what the session believes; the host can
    /// disagree, and on a short markless-SSH pane it does - <c>TerminalPane</c> hides the overlay host
    /// outright when the conservative anchor check produces no layout, and drops it to zero opacity
    /// while a placement correction settles. A <c>PassivePopup</c> is not a user-requested surface, so
    /// neither bypass applies to it, and without this term a passive popup at zero pixels could own the
    /// user's <c>Enter</c>: nothing on screen, and the command line does not submit.
    /// <see cref="_renderedSurfaceProbe"/> is the host answering "is any of this actually rendered".
    /// </para>
    /// </remarks>
    public bool IsAcceptOnEnterArmed =>
        ViewModel.IsVisible &&
        _renderedSurfaceProbe() &&
        _state.AllowsAcceptOnEnter(ViewModel.IsPopupOpen, GetSelectedSuggestion() != null);

    /// <summary>
    /// Whether <c>Up</c> currently belongs to Command Assist rather than to the shell's history recall.
    /// See <see cref="AssistSessionStateMachine.AllowsSelectionUp"/>.
    /// </summary>
    /// <remarks>
    /// Reads the same way <see cref="IsAcceptOnEnterArmed"/> does, for the same reason: the App's key
    /// interceptor asks this rather than deciding for itself, so there is one implementation of the
    /// rule. The rendered-surface term is deliberately <em>not</em> repeated here - an <c>Up</c> that
    /// only moves a selection cannot cost the user a submitted command, and refusing it on an invisible
    /// surface would hand the shell a history recall in the middle of a browse the user can see coming
    /// back the moment the correction pass settles.
    /// </remarks>
    public bool IsSelectionUpOwned =>
        ViewModel.IsVisible &&
        _state.AllowsSelectionUp(ViewModel.IsPopupOpen);

    /// <summary>
    /// The host's answer to "is the assist overlay actually rendered" changed, so republish everything
    /// derived from it - the hint strip in particular.
    /// </summary>
    /// <remarks>
    /// Without this the hint strip would lag the routing decision by one view-model change: the probe is
    /// pulled during <see cref="CommandAssistBarViewModel.SyncPresentationState"/>, which only runs when
    /// a view-model property is written, and the host's overlay visibility moves on render passes that
    /// write nothing. The lag is in the safe direction (the strip under-promises), but "the strip and
    /// the router cannot disagree" is the property this feature was given, so the host says when it
    /// changed its mind.
    /// </remarks>
    public void NotifyRenderedSurfaceVisibilityChanged() => ViewModel.SyncPresentationState();

    /// <summary>
    /// Whether the surface on screen is one the user asked for, which no placement heuristic may
    /// hide. See <see cref="AssistSessionStateMachine.IsUserRequestedSurface"/>.
    /// </summary>
    public bool IsUserRequestedSurface => ViewModel.IsVisible && _state.IsUserRequestedSurface;

    public void ToggleAssist()
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return;
        }

        bool nextVisible = _state.ToggleSession(ViewModel.IsVisible) != AssistSessionState.Hidden;
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = nextVisible;
        if (nextVisible)
        {
            QueueRefreshSuggestions();
        }
    }

    /// <summary>
    /// Opens explicit history search (<c>Ctrl+R</c>).
    /// </summary>
    /// <remarks>
    /// The label distinguishes the two things this surface can be. With a readable command line the
    /// rows are filtered by it, and "History" says so. Without one - a markless session, a closed
    /// lifecycle gate - there is no query and there will never be one, so the rows are the recency
    /// list and typing will not narrow them. Calling that "History" too presents a filter box that
    /// cannot filter, and the user reads the first keystroke that changes nothing as a bug rather
    /// than as the documented degraded behavior.
    /// </remarks>
    public bool OpenHistorySearch()
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        _state.OpenSearch();
        ViewModel.ModeLabel = TryReadQuerySnapshot() == null ? "History - recent" : "History";
        ViewModel.IsPopupOpen = true;
        ViewModel.IsVisible = true;
        QueueRefreshSuggestions();
        return true;
    }

    public bool OpenHelp()
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        _ = OpenHelpAsync();
        return true;
    }

    /// <summary>
    /// The user did something at the prompt that probably changed the command line - typed a
    /// character, pressed Backspace. Triggers a refresh; carries no text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what a keystroke means to Command Assist now. The character itself is
    /// not interesting: it is already on the screen, and so is everything the shell did that no
    /// keystroke describes - the <c>Ctrl+U</c> that emptied the line, the Up-arrow that recalled a
    /// command, the Tab the shell completed. V1 mirrored the four events it could see and was wrong
    /// about the line the moment any of the others happened.
    /// </para>
    /// <para>
    /// Backspace deliberately has no separate entry point and no "is there anything to delete"
    /// guard. Whether the line got shorter is the grid's business.
    /// </para>
    /// <para>
    /// <strong>History search filters rather than closing.</strong> A keystroke used to mean the same
    /// thing in every mode - drop to a Suggest bubble with the popup shut - and in
    /// <c>Ctrl+R</c> that read as the surface being dismissed by the first character of the thing the
    /// user opened it to look for (the owner's report: "when Ctrl+R shows history, if I type it just
    /// hides that instead of searching"). In Search mode the surface now stands still and the
    /// refresh does the work: <see cref="AssistSessionStateMachine.ObserveTypedInput"/> keeps the
    /// state, so <see cref="QueueRefreshSuggestions"/> re-ranks the history-only Search scope against
    /// the query the pass reads off the grid. The keystroke still reaches the shell - there is no
    /// search box, and the query is the command line - so the debounce that coalesces passive typing
    /// applies here too.
    /// </para>
    /// </remarks>
    public void NotifyInputActivity()
    {
        if (_context.IsAltScreenActive)
        {
            return;
        }

        _state.ObserveTypedInput();

        if (_state.Mode == CommandAssistMode.Search)
        {
            // Held rather than merely left alone. Nothing else in this method can reach a Search
            // session (an explicit surface is exempt from the passive suppression below, and its
            // visibility does not depend on the rows a refresh happens to produce), so stating the
            // invariant here is what keeps a filter keystroke from ever being the thing that takes
            // the popup down. The label is deliberately not rewritten: it was decided when the
            // surface opened, from a grid read this path must not repeat per keystroke.
            ViewModel.IsPopupOpen = true;
            ViewModel.IsVisible = true;
            QueueRefreshSuggestions(isTypingTriggered: true);
            return;
        }

        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;

        // Escape on this command line means the passive bubble stays down until the line is
        // submitted, so a keystroke after it must not put the surface back up - not even the rows
        // that were already ranked. See AssistSessionStateMachine.IsPassiveSurfaceSuppressed.
        if (!_state.AllowsPassiveSuggestions)
        {
            _suggestionOrchestrator.CancelPending();
            ViewModel.IsVisible = false;
            ClearSuggestionSurface();
            return;
        }

        // Visibility follows the rows that are already up, not the rows this refresh will produce.
        // Setting it true here and false when the pass lands is the flash that #232's predecessor
        // shipped; the pass sets it for real in ApplyRefreshOutcome.
        ViewModel.IsVisible = ViewModel.HasSuggestions;
        QueueRefreshSuggestions(isTypingTriggered: true);
    }

    /// <summary>
    /// The user pasted into the terminal. Suppresses the current submission and triggers a refresh;
    /// like <see cref="NotifyInputActivity"/> it carries no text.
    /// </summary>
    /// <remarks>
    /// Suppression is the one piece of the old paste handling that had to survive the shadow
    /// buffer's deletion, and it is not a query fact: it says the text on the line was not composed
    /// here, which stops it being written to history as though it were and stops a suggestion being
    /// spliced into it. The grid can read pasted text perfectly well; what it cannot see is where
    /// the text came from.
    /// </remarks>
    public void NotifyPastedInput()
    {
        _state.ObservePastedText();
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;

        if (!_state.AllowsPassiveSuggestions)
        {
            _suggestionOrchestrator.CancelPending();
            ViewModel.IsVisible = false;
            ClearSuggestionSurface();
            return;
        }

        ViewModel.IsVisible = !_context.IsAltScreenActive && ViewModel.HasSuggestions;

        // Debounced like typing rather than treated as an explicit action: a paste is a line edit, a
        // multi-line paste arrives as several of them, and the user did not ask for a surface.
        QueueRefreshSuggestions(isTypingTriggered: true);
    }

    /// <summary>
    /// The user pressed Enter. Runs heuristic history capture over <paramref name="submittedText"/>
    /// and resets the session for the next command line.
    /// </summary>
    /// <param name="submittedText">
    /// What the host believes was submitted, read from the grid at the moment Enter was observed, or
    /// <see langword="null"/> when there was nothing truthful to read. Passed in rather than pulled
    /// from a snapshot here because the host owns the timing: Enter reaches the PTY before this runs,
    /// and the closer the read sits to the keypress the smaller the window in which the shell has
    /// already begun repainting.
    /// </param>
    /// <remarks>
    /// <para>
    /// A markless session passes <see langword="null"/> and captures nothing. That is a deliberate
    /// loss and the alternative was worse: V1's Enter-time capture read the shadow buffer, so any
    /// line the user had edited with the keys the mirror could not see was written to persistent
    /// history <em>wrong</em>. Recording no command is recoverable; recording a command the user
    /// never ran is not. Instrumented sessions are unaffected - the first command is captured from
    /// the grid here, and every command after it from the <c>OSC 133;C</c> payload, which is what
    /// <see cref="AssistSessionContext.IsStructuredCaptureActive"/> stands the heuristic down for.
    /// </para>
    /// </remarks>
    public async Task HandleEnterAsync(string? submittedText = null)
    {
        try
        {
            await _capturePipeline.CaptureSubmissionAsync(
                submittedText ?? string.Empty,
                _state.IsCurrentSubmissionSuppressed);
        }
        catch
        {
            // Capture is best-effort and Enter must reach the shell regardless. CapturePipeline
            // already swallows its own failures, so this only backstops a future change to it.
        }
        finally
        {
            ResetSubmissionState();
        }
    }

    /// <summary>
    /// The live command line, or <see langword="null"/> when the session is markless, the shell is
    /// not in its line editor, or the grid cannot be read.
    /// </summary>
    /// <remarks>
    /// Read fresh on every call rather than cached from the last refresh. Callers use it to decide
    /// what to send to a live terminal, and the line may have moved since the rows on screen were
    /// ranked.
    /// </remarks>
    public AssistQuerySnapshot? TryReadQuerySnapshot() => _suggestionOrchestrator.TryReadQuery();

    public void UpdateSessionContext(
        string? shellKind,
        string? workingDirectory,
        string? profileId,
        string? sessionId,
        string? hostId,
        bool isRemote,
        bool isShellIntegrated = false)
    {
        _context.UpdateSession(
            shellKind,
            workingDirectory,
            profileId,
            sessionId,
            hostId,
            isRemote,
            isShellIntegrated);
    }

    public void SetShellIntegrationEnabled(bool isEnabled)
    {
        _context.SetShellIntegrationEnabled(isEnabled);
    }

    /// <summary>
    /// Applies the two Command Assist sub-settings: whether history is in play at all, and whether the
    /// passive typing bubble may draw on it.
    /// </summary>
    /// <remarks>
    /// Called from the host whenever settings are applied, including the first initialization. Neither
    /// flag gates the feature - see <see cref="AssistSessionContext.IsHistoryEnabled"/> for what
    /// changed in V2 Phase 3b and why.
    /// </remarks>
    public void SetFeaturePolicy(bool isHistoryEnabled, bool isPassiveBubbleEnabled)
    {
        _context.SetFeaturePolicy(isHistoryEnabled, isPassiveBubbleEnabled);
    }

    /// <summary>
    /// Replaces the key names the hint strip renders, so a rebound shortcut is advertised correctly.
    /// </summary>
    /// <remarks>
    /// The labels come from the App's shortcut catalogue, which this assembly cannot see (it must not
    /// reference Avalonia, and the catalogue's bindings are <c>Avalonia.Input</c> chords). So the host
    /// resolves them and pushes them here; the defaults reproduce the shipped strings exactly, which is
    /// what a controller with no host - a test, the MCP surface - keeps showing.
    /// </remarks>
    public void SetShortcutHintLabels(AssistShortcutHintLabels labels)
    {
        ViewModel.ShortcutHintLabels = labels;
    }

    public void Dismiss()
    {
        _suggestionOrchestrator.CancelPending();
        _state.Dismiss();
        ViewModel.IsVisible = false;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
    }

    /// <summary>
    /// The user pressed Escape. In the passive flow this is staged: the first press closes the popup
    /// back to the bubble it came from, and the second takes the surface down and keeps it down for the
    /// rest of this command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The staging (dogfood round 4, item 3).</strong> <c>Down</c> opens the passive popup in
    /// one keystroke, so Escape needs to be able to undo one keystroke; before this it undid the whole
    /// command line's worth of assist, and the owner's report was that Escape "kills everything". The
    /// scope of the first stage is <see cref="AssistSessionStateMachine.TryCollapsePopupToBubble"/>,
    /// which is passive-popup-only - the surfaces the user summoned by name still close outright on the
    /// first press. The second press lands here as an ordinary bubble Escape, unchanged.
    /// </para>
    /// <para>
    /// The per-command scope is V2 Phase 3b's, and it is what makes the passive bubble dismissible in
    /// any useful sense: before it, Escape hid a surface the next keystroke rebuilt. Explicit surfaces
    /// are unaffected - <c>Ctrl+R</c> and <c>Ctrl+Space</c> still open after an Escape - because
    /// suppression is only consulted for a refresh the user did not ask for.
    /// </para>
    /// <para>
    /// Returns <see langword="false"/> when nothing is on screen, which is how Escape reaches the
    /// shell (where it means "cancel this line" in every line editor) on an untouched prompt.
    /// </para>
    /// <para>
    /// <strong>The debounce window is not "nothing on screen" (PR #293 review, non-blocking 1).</strong>
    /// For the 75 ms after a keystroke the view-model still says invisible while a pass is on its way,
    /// and the old early-out meant Escape in that window did nothing at all: the pass landed and the
    /// bubble appeared, after the user had declined it. So a pending pass is cancelled and the
    /// suppression flag taken even though there is nothing to hide - and the return value stays
    /// <see langword="false"/>, because a key pressed at a prompt with nothing on screen belongs to the
    /// shell. Suppressing without handling is the whole point: the user gets both the shell's Escape and
    /// the absence of a bubble.
    /// </para>
    /// </remarks>
    public bool HandleEscape()
    {
        if (!ViewModel.IsVisible)
        {
            if (_suggestionOrchestrator.HasPassInFlight)
            {
                _suggestionOrchestrator.CancelPending();
                _state.DismissForCurrentCommand();
                ClearSuggestionSurface();
            }

            return false;
        }

        // Stage one: an uninvited popup collapses back to its bubble. Nothing else moves - the rows,
        // the selection and the visibility all survive, which is what makes the suggestion still be
        // there afterwards. No CancelPending either: a refresh already on its way is still wanted by
        // the bubble that stays on screen.
        if (_state.TryCollapsePopupToBubble())
        {
            ViewModel.IsPopupOpen = false;
            return true;
        }

        _suggestionOrchestrator.CancelPending();
        _state.DismissForCurrentCommand();
        ViewModel.IsVisible = false;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
        return true;
    }

    public bool MoveSelectionDown()
    {
        if (_suggestions.Count == 0)
        {
            return false;
        }

        int nextIndex = ViewModel.SelectedIndex < 0
            ? 0
            : Math.Min(ViewModel.SelectedIndex + 1, _suggestions.Count - 1);

        return SetSelectedIndex(nextIndex);
    }

    /// <summary>
    /// Moves the selection up one row. At the top of the list this is a no-op.
    /// </summary>
    /// <remarks>
    /// The clamp used to be <c>SetSelectedIndex(0)</c>, which is not a no-op: it opens the popup and
    /// arms <c>Enter</c> as side effects (see <see cref="SetSelectedIndex"/>). Combined with <c>Up</c>
    /// being assist-owned in the passive states, that is the PR #290 review's second blocker - the key
    /// the user pressed to reach their shell history built the surface that then ate their <c>Enter</c>.
    /// <c>Up</c> is no longer routed here in those states, and this no longer creates a browse state
    /// out of nothing even when it is.
    /// </remarks>
    public bool MoveSelectionUp()
    {
        if (_suggestions.Count == 0 || ViewModel.SelectedIndex <= 0)
        {
            return false;
        }

        return SetSelectedIndex(ViewModel.SelectedIndex - 1);
    }

    /// <summary>
    /// Selects a row by index because the user pointed at it. Opens the popup the same way
    /// <c>Up</c>/<c>Down</c> do, so a click and an arrow key leave the session in the same state -
    /// including the state that arms <c>Enter</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> for an index no row occupies, which is how a click that arrives one
    /// frame after the list shrank resolves.
    /// </returns>
    public bool TrySelectSuggestionAt(int index) => SetSelectedIndex(index);

    public bool TryGetInsertionText(out string? insertionText)
    {
        AssistSuggestion? selected = GetSelectedSuggestion();
        if (selected == null || _state.IsCurrentSubmissionSuppressed || _context.IsAltScreenActive)
        {
            insertionText = null;
            return false;
        }

        insertionText = selected.InsertText;
        return true;
    }

    public bool TryAcceptSelection(out string? insertionText)
    {
        if (!TryGetInsertionText(out insertionText) || string.IsNullOrWhiteSpace(insertionText))
        {
            return false;
        }

        // No query write here. Accepting a row asks the host to send a delta to the terminal; the
        // line's new contents are then whatever the shell paints, and the next refresh reads that.
        // Writing the accepted text into QueryText was V1 predicting the outcome of an edit it did
        // not perform, and it was wrong whenever the send failed or the shell transformed the input.
        _state.AcceptSelection();
        ViewModel.ModeLabel = "Suggest";
        ViewModel.IsPopupOpen = false;
        Dismiss();
        return true;
    }

    public bool CanTogglePinSelection()
    {
        AssistSuggestion? selected = GetSelectedSuggestion();
        return SnippetStore != null &&
               selected != null &&
               selected.Type is AssistSuggestionType.History or AssistSuggestionType.Snippet;
    }

    public async Task HandleCommandFinishedAsync(int? exitCode)
    {
        await _capturePipeline.CompleteSubmissionAsync(exitCode);
    }

    public async Task<bool> OpenHelpAsync(string? queryText = null, string? selectedText = null)
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        // Falls back to grid truth, not to the view-model. The view-model holds whatever the last
        // ranking pass wrote, which is a frame behind at best and stale help-token extraction at
        // worst; in a markless session there is no query and the recognized command comes from the
        // selection alone, which is the degraded-mode contract.
        //
        // TextBeforeCursor, for the reason ranking uses it (PR #293 review, blocker 1): the whole
        // painted line includes PSReadLine's inline prediction, so Help on `ec` used to extract its
        // command token from whatever the shell had guessed - answering a question about `git` while the
        // user was typing `ec`. The token comes from the characters the user put there.
        string effectiveQuery = queryText ?? TryReadQuerySnapshot()?.TextBeforeCursor ?? string.Empty;
        CommandAssistContextSnapshot snapshot = _context.CreateSnapshot(
            effectiveQuery,
            selectedText);

        // The one Help-side crossing into the seam. Everything downstream - including the bundled
        // catalogue - sees only what came out of the request factory.
        AssistContentRequest request = _requestFactory.CreateHelpRequest(
            AssistCapabilities.EnrichDocs,
            commandText: snapshot.QueryText,
            commandToken: snapshot.RecognizedCommand,
            shellKind: snapshot.ShellKind,
            workingDirectory: snapshot.WorkingDirectory,
            selectedText: snapshot.SelectedText,
            sessionId: snapshot.SessionId,
            isRemote: _context.IsRemote);

        IReadOnlyList<AssistContentResult> results = await _contentProviders.QueryAsync(request);

        // Docs before recipes across the whole chain, not per provider: the popup groups by row type
        // and always has, so a second provider's doc row belongs with the first's rather than after
        // its examples.
        IReadOnlyList<CommandHelpItem> docs = results.SelectMany(result => result.Docs).ToArray();
        IReadOnlyList<CommandHelpItem> recipes = results.SelectMany(result => result.Recipes).ToArray();
        IReadOnlyList<AssistSuggestion> suggestions = _resultBuilder.BuildCombined(
            Array.Empty<AssistSuggestion>(),
            docs,
            recipes,
            Array.Empty<CommandFixSuggestion>());

        // First credit wins, matching provider order. A result carries its own attribution, so the
        // credit cannot outlive or precede the content it belongs to - which is what the old "read
        // the docs provider's property after the awaits" dance was working around.
        string? attribution = results
            .Select(result => result.Attribution)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        string emptyStateText = _contentProviders.HasProviderFor(AssistCapabilities.EnrichDocs)
            ? AssistEmptyStates.NoLocalHelp
            : AssistEmptyStates.ForMissingProvider(AssistCapabilities.EnrichDocs);

        _dispatch(() => ApplyHelperSuggestions(
            _modeRouter.ChooseModeForHelpRequest(),
            effectiveQuery,
            suggestions,
            emptyStateText,
            openPopup: true,
            attribution: attribution));

        return true;
    }

    /// <summary>
    /// Whether anything is configured to answer <paramref name="capability"/>.
    /// </summary>
    /// <remarks>
    /// The question an empty state asks, exposed because a future Settings page and a future
    /// Ask-AI entry point both need it before they render anything. See
    /// <see cref="AssistEmptyStates"/> for what is and is not reachable in the shipped UI today.
    /// </remarks>
    public bool HasContentProviderFor(AssistCapabilities capability)
        => _contentProviders.HasProviderFor(capability);

    public async Task<bool> ExplainSelectionAsync(string? selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            return false;
        }

        return await OpenHelpAsync(selectedText: selectedText);
    }

    public async Task<bool> HandleCommandFailureAsync(CommandFailureContext context)
    {
        if (_context.IsAltScreenActive)
        {
            ViewModel.IsVisible = false;
            return false;
        }

        // The one Fix-side crossing into the seam. The pane already redacted the output tail at the
        // VT boundary; the factory redacts everything again regardless, because a guarantee that
        // depends on the caller having remembered is not one. See RedactedText.
        AssistContentRequest request = _requestFactory.CreateFixRequest(context, _context.SessionId);
        IReadOnlyList<AssistContentResult> results = await _contentProviders.QueryAsync(request);
        CommandFixSuggestion[] fixes = results.SelectMany(result => result.Fixes).ToArray();

        double highestConfidence = fixes.Length == 0 ? 0 : fixes.Max(item => item.Confidence);
        CommandAssistMode mode = _modeRouter.ChooseModeForFailure(highestConfidence);
        IReadOnlyList<AssistSuggestion> suggestions = _resultBuilder.BuildFixSuggestions(fixes);

        // The typo suppression's second signal (UX-polish round, issue 5). Exit code 127 is handled in
        // CapturePipeline where the completion patch already lands, but PowerShell reports 1 for an
        // unresolved name, so on Windows the only thing that knows a typo happened is the recogniser
        // that just read "The term 'gti' is not recognized" out of the output tail. Thread it back to
        // the entry the pipeline captured for this very command, before anything can offer it again.
        //
        // Best-effort and deliberately not awaited into the surface decision below: failing to mark a
        // typo must not also cost the user the fix suggestion for it.
        //
        // The token is threaded too, when the recogniser found the unresolved name in the command
        // position: the classification is a fact about the name rather than about this one execution,
        // so it is applied to every older entry that starts with it (dogfood round 4, item 4a). Without
        // that, the owner's history - captured before the flag existed - kept offering him every
        // `gti status` he had ever run until he retyped each one.
        CommandFixSuggestion[] notFound = fixes.Where(item => item.IsCommandNotFound).ToArray();
        if (notFound.Length > 0)
        {
            string? unresolvedToken = notFound
                .Select(item => item.UnresolvedCommandToken)
                .FirstOrDefault(token => !string.IsNullOrWhiteSpace(token));

            await _capturePipeline.MarkLastCommandInvalidAsync(unresolvedToken);
        }

        // Kept symmetrical with Help even though neither Fix branch below can currently render it:
        // reaching Fix mode requires a confidence >= 0.8, which requires at least one row, and the
        // Suggest branch returns early when there are none. That was true of "No likely local fix
        // found." before this phase too - the string has never been on screen. The right of the two
        // is computed here rather than the wrong one being hard-coded, so a future router change
        // shows the honest sentence instead of the convenient one.
        string emptyStateText = _contentProviders.HasProviderFor(AssistCapabilities.SuggestFix)
            ? AssistEmptyStates.NoLocalFix
            : AssistEmptyStates.ForMissingProvider(AssistCapabilities.SuggestFix);

        if (mode == CommandAssistMode.Fix)
        {
            _dispatch(() => ApplyHelperSuggestions(
                CommandAssistMode.Fix,
                context.CommandText,
                suggestions,
                emptyStateText,
                openPopup: true));
            return true;
        }

        // The noise floor (UX-polish round, issue 4). This used to be `suggestions.Count == 0`, i.e.
        // every failing command in the session got a bubble whatever the ladder had managed to infer.
        // See CommandAssistModeRouter.ShouldSurfacePassiveFix for what "actionable" means and why the
        // suppressed rows are still reachable by an explicit Ctrl+Space.
        if (!_modeRouter.ShouldSurfacePassiveFix(fixes))
        {
            return false;
        }

        _dispatch(() => ApplyHelperSuggestions(
            CommandAssistMode.Fix,
            context.CommandText,
            suggestions,
            emptyStateText,
            openPopup: false));
        return false;
    }

    /// <summary>
    /// Consumes an OSC 133 event: moves the command-input lifecycle gate, then lets the capture
    /// pipeline do whatever the event means for history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate moves here rather than inside <see cref="CapturePipeline"/> because it is not a
    /// capture concern - it is what makes grid-truth reading legal, and the pipeline would be the
    /// wrong place to look for it. <c>B</c> opens the window, <c>C</c> and <c>D</c> close it; see
    /// <see cref="AssistSessionContext.IsAcceptingCommandInput"/> for why both closers are needed
    /// and why nothing else opens it.
    /// </para>
    /// <para>
    /// <strong>The two session transitions here are PR #293 review blocker 2.</strong> Before it, the
    /// session's own bookkeeping only ever moved on keys this controller saw, so a command line that began
    /// or ended any other way left the session out of step with the shell:
    /// </para>
    /// <list type="bullet">
    /// <item><c>B</c> now calls <see cref="AssistSessionStateMachine.BeginCommandLine"/>, which is what
    /// ends the per-command Escape suppression. See that method for the accepted caveat about prompt
    /// repaints.</item>
    /// <item><c>C</c> now normalizes the surface the same way a local <c>Enter</c> does. <c>C</c> is the
    /// shell saying "this line has been accepted", and it arrives for submissions no keystroke of ours
    /// described - a pasted line ending in a newline, a broadcast send to every pane, an agent-sent
    /// command, <c>Enter</c> pressed while the pane did not have focus. Without this the bubble ranked for
    /// the line that just ran stayed on screen over the running command's output.</item>
    /// </list>
    /// <para>
    /// The surface write goes through <see cref="_dispatch"/>: this runs on the pane's serialized event
    /// dispatcher, which is not the UI thread, and the view-model is bound.
    /// </para>
    /// </remarks>
    public async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
    {
        switch (shellEvent.Type)
        {
            case ShellIntegrationEventType.CommandStarted:
                _context.OpenCommandInputWindow();
                _state.BeginCommandLine();
                break;
            case ShellIntegrationEventType.CommandAccepted:
                _context.CloseCommandInputWindow();
                _dispatch(ResetSubmissionState);
                break;
            case ShellIntegrationEventType.CommandFinished:
                _context.CloseCommandInputWindow();
                break;
        }

        await _capturePipeline.HandleShellIntegrationEventAsync(shellEvent);
    }

    public async Task<bool> TogglePinSelectionAsync()
    {
        if (!CanTogglePinSelection())
        {
            return false;
        }

        ISnippetStore snippetStore = SnippetStore!;
        AssistSuggestion? selected = GetSelectedSuggestion();
        if (selected == null)
        {
            return false;
        }

        try
        {
            IReadOnlyList<CommandSnippet> snippets = await snippetStore.GetAllAsync();
            CommandSnippet? existing = snippets.FirstOrDefault(x => x.Id == selected.Id) ??
                                       snippets.FirstOrDefault(x =>
                                           string.Equals(x.CommandText, selected.InsertText, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(x.ShellKind ?? string.Empty, _context.ShellKind ?? string.Empty, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                await snippetStore.UpsertAsync(existing with
                {
                    IsPinned = !existing.IsPinned,
                    LastUsedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                var snippet = new CommandSnippet(
                    Id: Guid.NewGuid().ToString("N"),
                    Name: selected.DisplayText,
                    CommandText: selected.InsertText,
                    Description: selected.Description,
                    ShellKind: _context.ShellKind,
                    WorkingDirectory: _context.WorkingDirectory,
                    IsPinned: true,
                    CreatedAt: DateTimeOffset.UtcNow,
                    LastUsedAt: selected.LastUsedAt);

                await snippetStore.UpsertAsync(snippet);
            }

            QueueRefreshSuggestions();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void HandleAltScreenChanged(bool isAltScreenActive)
    {
        _context.SetAltScreenActive(isAltScreenActive);
        if (!isAltScreenActive)
        {
            return;
        }

        _suggestionOrchestrator.CancelPending();
        _state.HideForAltScreen();
        ViewModel.IsVisible = false;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
    }

    private void QueueRefreshSuggestions(bool isTypingTriggered = false)
    {
        if (!_state.AllowsSuggestionRefresh || !_state.AllowsPassiveSuggestions)
        {
            return;
        }

        _suggestionOrchestrator.Refresh(_state.Mode, _state.IsExplicitSession, isTypingTriggered);
    }

    /// <summary>
    /// Applies a ranking pass on the dispatcher thread. Outcomes for a mode the session has already
    /// left are dropped: the orchestrator's cancellation covers superseded queries, this covers a
    /// pass that was in flight when Help or Fix took the surface over.
    /// </summary>
    private void ApplyRefreshOutcome(SuggestionRefreshOutcome outcome)
    {
        if (_state.Mode != outcome.RequestedMode)
        {
            return;
        }

        // The one place a Suggest/Search query reaches the view-model. The pass read it off the
        // grid, so this is also where the surface catches up with edits no keystroke reported.
        _isInsertionAvailable = outcome.InsertionAppearsAvailable;
        ViewModel.QueryText = outcome.Query;
        PublishSessionStatus();

        // A ranking pass shows the user's own history, snippets and paths - nothing licensed. If a
        // Help surface left a credit behind, this is where it goes.
        ViewModel.AttributionText = string.Empty;

        if (outcome.Faulted)
        {
            ClearSuggestionSurface();
        }
        else
        {
            _suggestions.Clear();
            _suggestions.AddRange(outcome.Suggestions);

            // Reset to the top row on every pass, fzf-style. The rows are a different list than the
            // one the old selection indexed into, so carrying the index over would move the highlight
            // to an unrelated command; index 0 is the best match for what the user has typed so far,
            // which is the row they mean if they stop typing and press the accept key.
            ViewModel.SelectedIndex = _suggestions.Count > 0 ? 0 : -1;

            // The empty state a filtered history search needs: see AssistEmptyStates.NoHistoryMatch.
            // Only with a query - a Search pass that read no query at all is the degraded recency list,
            // and "no matching commands" would blame the user's typing for a markless session.
            bool isEmptyHistorySearch = outcome.RequestedMode == CommandAssistMode.Search &&
                                        _suggestions.Count == 0 &&
                                        !string.IsNullOrEmpty(outcome.Query);
            ViewModel.EmptyStateText = isEmptyHistorySearch ? AssistEmptyStates.NoHistoryMatch : string.Empty;
            ViewModel.ShowEmptyState = isEmptyHistorySearch;
            SyncSuggestionViewModel();
        }

        if (outcome.RequestedMode != CommandAssistMode.Suggest)
        {
            return;
        }

        _state.ClosePopupAfterRefresh();
        ViewModel.IsPopupOpen = false;
        ViewModel.IsVisible = _state.IsExplicitSession || _suggestions.Count > 0;
    }

    private void ResetSubmissionState()
    {
        _suggestionOrchestrator.CancelPending();
        _state.CompleteSubmission();
        ViewModel.QueryText = string.Empty;
        ViewModel.IsPopupOpen = false;
        ClearSuggestionSurface();
        ViewModel.IsVisible = false;
    }

    /// <summary>Empties every row-derived view-model field and the backing suggestion list.</summary>
    private void ClearSuggestionSurface()
    {
        ViewModel.TopSuggestionText = string.Empty;
        ViewModel.SelectedIndex = -1;
        ViewModel.SelectedBadgesText = string.Empty;
        ViewModel.SelectedMetadataText = string.Empty;
        ViewModel.SelectedDescriptionText = string.Empty;
        ViewModel.EmptyStateText = string.Empty;
        ViewModel.ShowEmptyState = false;
        ViewModel.HasSuggestions = false;
        ViewModel.AttributionText = string.Empty;
        ViewModel.Suggestions.Clear();
        _suggestions.Clear();
    }

    private bool SetSelectedIndex(int index)
    {
        if (index < 0 || index >= _suggestions.Count)
        {
            return false;
        }

        ViewModel.SelectedIndex = index;
        if (!ViewModel.IsPopupOpen)
        {
            _state.OpenPopupForSelection();
            ViewModel.IsPopupOpen = true;
        }

        // Selection only. Rebuilding the row list here (which is what this used to do) replaces every
        // container under the pointer, so hover died on each arrow key and the scroll position jumped
        // back to the top - see CommandAssistSuggestionItemViewModel.
        SyncSelectionState();
        return true;
    }

    /// <summary>
    /// Republishes the session facts the surface displays about itself, as opposed to about its rows.
    /// </summary>
    /// <remarks>
    /// Today that is just the integration chip. Pulled from
    /// <see cref="AssistSessionContext.IsShellIntegrationLive"/> at publish time rather than pushed
    /// when it changes, for the reason the hint-strip probes give: the flag moves on marker
    /// observations deep in the capture path, and a pushed copy would be stale at whichever call site
    /// forgot to update it.
    /// </remarks>
    private void PublishSessionStatus()
    {
        ViewModel.IsShellIntegrationLive = _context.IsShellIntegrationLive;
    }

    private AssistSuggestion? GetSelectedSuggestion()
    {
        return ViewModel.SelectedIndex >= 0 && ViewModel.SelectedIndex < _suggestions.Count
            ? _suggestions[ViewModel.SelectedIndex]
            : null;
    }

    /// <summary>
    /// Rebuilds the row list from <see cref="_suggestions"/>. Call only when the rows themselves
    /// changed; <see cref="SyncSelectionState"/> covers a selection move.
    /// </summary>
    private void SyncSuggestionViewModel()
    {
        ViewModel.Suggestions.Clear();

        for (int i = 0; i < _suggestions.Count; i++)
        {
            AssistSuggestion suggestion = _suggestions[i];
            ViewModel.Suggestions.Add(new CommandAssistSuggestionItemViewModel(
                displayText: suggestion.DisplayText,
                descriptionText: suggestion.Description ?? string.Empty,
                badgesText: string.Join("  ", suggestion.Badges),
                metadataText: BuildMetadataText(suggestion),
                isSelected: i == ViewModel.SelectedIndex,
                type: suggestion.Type));
        }

        SyncSelectionState();
    }

    /// <summary>
    /// Republishes everything that depends on <em>which</em> row is selected, mutating the existing
    /// rows rather than replacing them.
    /// </summary>
    private void SyncSelectionState()
    {
        AssistSuggestion? selected = GetSelectedSuggestion();
        ViewModel.TopSuggestionText = selected?.DisplayText ?? string.Empty;
        ViewModel.SelectedBadgesText = selected == null ? string.Empty : string.Join("  ", selected.Badges);
        ViewModel.SelectedMetadataText = selected == null ? string.Empty : BuildMetadataText(selected);
        ViewModel.SelectedDescriptionText = selected?.Description ?? string.Empty;
        ViewModel.HasSuggestions = _suggestions.Count > 0;

        for (int i = 0; i < ViewModel.Suggestions.Count; i++)
        {
            ViewModel.Suggestions[i].IsSelected = i == ViewModel.SelectedIndex;
        }
    }

    /// <summary>
    /// The metadata caption under a row: where it ran, how long ago, and how it ended.
    /// </summary>
    /// <remarks>
    /// The timestamp is relative since the UX-polish round. <c>Used 2026-08-04 15:23</c> made the
    /// reader do arithmetic to answer the only question a <c>Ctrl+R</c> row raises, and it was the
    /// one field that could have made the new recency ordering legible. See
    /// <see cref="AssistRelativeTime"/>. The directory stays: it is the other half of the placement,
    /// and the "Same dir" badge is a boolean where this is the actual path.
    /// </remarks>
    private string BuildMetadataText(AssistSuggestion suggestion)
    {
        List<string> parts = new();
        if (!string.IsNullOrWhiteSpace(suggestion.WorkingDirectory))
        {
            parts.Add(suggestion.WorkingDirectory!);
        }

        if (suggestion.LastUsedAt.HasValue)
        {
            parts.Add(AssistRelativeTime.Format(suggestion.LastUsedAt.Value, Clock()));
        }

        if (suggestion.ExitCode.HasValue)
        {
            parts.Add(suggestion.ExitCode.Value == 0 ? "Exit 0" : $"Exit {suggestion.ExitCode.Value}");
        }

        return string.Join("  |  ", parts);
    }

    private void ApplyHelperSuggestions(
        CommandAssistMode mode,
        string? queryText,
        IReadOnlyList<AssistSuggestion> suggestions,
        string emptyStateText,
        bool openPopup,
        string? attribution = null)
    {
        _suggestionOrchestrator.CancelPending();
        _state.EnterHelperMode(mode, openPopup);

        // Written on every helper surface, including the ones that pass null, so the credit cannot
        // outlive the content it belongs to: a Fix popup opening over a Help popup would otherwise
        // keep crediting tldr-pages under rows that came from the error heuristics.
        ViewModel.AttributionText = suggestions.Count > 0 ? attribution ?? string.Empty : string.Empty;

        // One fresh read on a user action, so the hint strip is right about insertion for a Help row
        // too. No snapshot reads as available, for the reason on
        // SuggestionRefreshOutcome.InsertionAppearsAvailable - which is also the answer for Fix, published
        // after the line was submitted with the lifecycle gate already shut.
        _isInsertionAvailable = TryReadQuerySnapshot()?.IsUsableAsTypedPrefix ?? true;
        PublishSessionStatus();
        ViewModel.ModeLabel = mode.ToString();

        // Help and Fix put their subject in the query field: the command Help was asked about, the
        // command that failed. That is a caption for content the user asked for, not a claim about
        // what is on the command line - and Fix in particular is shown *after* the line was
        // submitted, when there is no command line to be truthful about.
        ViewModel.QueryText = queryText ?? string.Empty;
        ViewModel.IsPopupOpen = openPopup;
        ViewModel.IsVisible = true;
        ViewModel.EmptyStateText = suggestions.Count == 0 ? emptyStateText : string.Empty;
        ViewModel.ShowEmptyState = suggestions.Count == 0;

        _suggestions.Clear();
        _suggestions.AddRange(suggestions);
        ViewModel.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
        SyncSuggestionViewModel();
    }
}
