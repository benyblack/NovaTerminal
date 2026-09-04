using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Turns "something happened at the prompt" into a ranked list: reads the query out of the terminal
/// grid, resolves which sources are in scope for the current session, fetches candidates off the UI
/// thread, ranks them with the one suggestion engine, and hands the result back through the
/// dispatcher - unless a newer pass has superseded it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The pass owns the query (Phase 1c).</strong> Callers no longer hand one in; they say
/// "refresh" and the pass resolves the query itself, from
/// <see cref="AssistSessionContext.IsAcceptingCommandInput"/> plus the grid provider. A keystroke is
/// a <em>trigger</em>, not a source: what the user typed is already on screen, and the screen also
/// knows about the arrow keys, the <c>Ctrl+U</c>, the history recall and the Tab completion that no
/// keystroke mirror ever saw.
/// </para>
/// <para>
/// <strong>Why the read happens here and not at the keystroke.</strong> The buffer's write lock is
/// taken per written character, so a read racing a prompt repaint (<c>\r</c>, erase-to-end, reprint)
/// can legitimately observe a half-erased line. Reading inside the pass puts the read behind the
/// queue hop the refresh already makes, and the per-pass cancellation below means that when several
/// keystrokes arrive together only the last pass's read is applied.
/// </para>
/// <para>
/// <strong>The debounce (V2 Phase 3b, task 1).</strong> Phase 0c deferred it here deliberately -
/// "the debounce is a policy decision and Phase 3 owns it" - and Phase 3b is where the policy
/// arrives, because the passive bubble makes the cost of a per-keystroke pass user-visible for the
/// first time. A typing-triggered pass now waits
/// <see cref="DefaultPassiveRefreshDebounce"/> before doing any work; the next keystroke cancels it
/// through the same <see cref="CancellationTokenSource"/> that already handled supersession, so a
/// burst of <em>n</em> keystrokes costs one grid read, one store recall and one ranking pass rather
/// than <em>n</em> of each.
/// </para>
/// <para>
/// It also closes the echo race that <c>CommandAssist_ShellIntegration_Gaps.md</c> documents as the
/// residual staleness (#286): the pass whose read beat the shell's echo of the last character was
/// racing that echo by microseconds, and 75 ms is several orders of magnitude more than a local echo
/// needs. The window is *narrowed to near-zero rather than closed*, and the distinction is worth
/// keeping honest - a remote shell whose echo takes longer than the debounce can still be read one
/// character stale, and insertion still refuses on <c>TerminalPane._hasUnechoedInput</c> rather than
/// trusting the delay. What is gone is the every-keystroke-is-a-race property.
/// </para>
/// <para>
/// Explicit passes are <em>not</em> debounced. <c>Ctrl+R</c>, <c>Ctrl+Space</c> and a pin toggle are
/// single user actions with nothing to coalesce, and 75 ms of nothing after a keypress the user
/// meant is exactly the latency the debounce exists to avoid spending.
/// </para>
/// <para>
/// Staleness is handled with a <see cref="CancellationTokenSource"/> per pass: queueing a refresh
/// cancels the one before it, and an outcome whose token is cancelled is dropped instead of applied.
/// This replaces the interlocked <c>_refreshVersion</c> counter that used to serve the same purpose.
/// Note that the token is deliberately <em>not</em> passed to the store or engine calls yet:
/// cancelling work already in flight is a behavior change Phase 0c did not want to make.
/// </para>
/// </remarks>
internal sealed class SuggestionOrchestrator
{
    /// <summary>
    /// Rows the popup renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// M4.2 shipped this at 5 because the popup was a fixed-height list with no way to reach a sixth
    /// row. V2 Phase 3a gave it a <c>ScrollViewer</c>, so the cap is now about how much ranked history
    /// is worth holding rather than about how much fits: 50 is a scrollable list a user will page
    /// through and abandon, not a screenful.
    /// </para>
    /// <para>
    /// The bubble still shows one row, so this cap costs nothing when the popup is closed.
    /// </para>
    /// </remarks>
    public const int MaxDisplayedSuggestions = 50;

    /// <summary>
    /// How many history candidates the ranking engine gets to choose from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wider than <see cref="MaxDisplayedSuggestions"/> on purpose. The store used to pre-rank with
    /// its own scoring function and hand back its own top five, so the engine only ever re-ordered
    /// a set the store had already picked. Now the store is a recall gate that truncates by
    /// recency, so it has to hand over enough rows for the engine's cwd / shell / profile / exit-code
    /// signals to matter - otherwise a relevant-but-older command could never reach the list.
    /// </para>
    /// <para>
    /// <strong>V2 Phase 3a: the empty-query path uses the same pool.</strong> It used to recall only
    /// <see cref="MaxDisplayedSuggestions"/> entries, on the argument that "most recent five" is
    /// already the answer when there is no text to score. That argument dies with context scoping: if
    /// the store hands over five rows, all of them from whichever session ran most recently, no amount
    /// of ranking can put *this* host's commands first - the host's commands were never in the set.
    /// The owner's "the list shows commands from all sessions indiscriminately" is that truncation as
    /// much as it is the absence of a ranking rule.
    /// </para>
    /// </remarks>
    private const int HistoryCandidatePoolSize = 200;

    /// <summary>
    /// How long a typing-triggered pass waits before doing any work, so that a burst of keystrokes
    /// produces one ranking pass rather than one per character.
    /// </summary>
    /// <remarks>
    /// 75 ms is the design doc's figure (Pillar 4). It sits below the ~100 ms at which a response
    /// stops feeling immediate and far above the microseconds a local shell needs to echo, which is
    /// what makes it useful for the echo race as well as for the allocation count.
    /// </remarks>
    public static readonly TimeSpan DefaultPassiveRefreshDebounce = TimeSpan.FromMilliseconds(75);

    /// <summary>
    /// How many characters the user has to have typed before an unasked-for bubble appears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The design doc's "after >= 2 typed characters". One character is not enough evidence of intent
    /// to justify a surface: at one character the ranked top-1 is essentially "the command you run
    /// most often that starts with that letter", which is noise dressed as a suggestion, and every
    /// prompt in an interactive session would grow a bubble the moment it was touched.
    /// </para>
    /// <para>
    /// Measured on the query the pass reads off the grid, not on a keystroke count, which is the same
    /// reason the pass owns the query at all: a line reached by <c>Ctrl+U</c>, a history recall or a
    /// Tab completion has a length the keystrokes never described. It applies to the passive path
    /// only - an explicit <c>Ctrl+Space</c> on an empty line is a request for the recency list and
    /// gets it.
    /// </para>
    /// </remarks>
    public const int MinPassiveQueryLength = 2;

    private readonly IHistoryStore _historyStore;
    private readonly ISnippetStore? _snippetStore;
    private readonly ISuggestionEngine _suggestionEngine;
    private readonly AssistSessionContext _context;
    private readonly Func<AssistQuerySnapshot?> _queryProvider;
    private readonly Action<Action> _dispatch;
    private readonly Action<SuggestionRefreshOutcome> _applyOutcome;
    private readonly TimeSpan _passiveRefreshDebounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private CancellationTokenSource? _refreshCts;
    private int _passesInFlight;

    public SuggestionOrchestrator(
        IHistoryStore historyStore,
        ISnippetStore? snippetStore,
        ISuggestionEngine suggestionEngine,
        AssistSessionContext context,
        Func<AssistQuerySnapshot?> queryProvider,
        Action<Action> dispatch,
        Action<SuggestionRefreshOutcome> applyOutcome,
        TimeSpan? passiveRefreshDebounce = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _historyStore = historyStore;
        _snippetStore = snippetStore;
        _suggestionEngine = suggestionEngine;
        _context = context;
        _queryProvider = queryProvider;
        _dispatch = dispatch;
        _applyOutcome = applyOutcome;
        _passiveRefreshDebounce = passiveRefreshDebounce ?? DefaultPassiveRefreshDebounce;

        // Injected so a test can assert the coalescing without spending wall-clock time on it, and
        // so the "no debounce" behavior stays reachable for the mutation check. Task.Delay is the
        // only production implementation.
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    /// <summary>
    /// Queues a ranking pass, superseding any pass still in flight. Returns immediately; the pass
    /// resolves its own query and the outcome arrives through the dispatcher.
    /// </summary>
    /// <param name="isTypingTriggered">
    /// Whether this pass was queued by a keystroke rather than by a user action that names a surface.
    /// Typing-triggered passes are debounced and gated on <see cref="MinPassiveQueryLength"/>.
    /// </param>
    public void Refresh(CommandAssistMode requestedMode, bool isExplicitSession, bool isTypingTriggered = false)
    {
        CancellationToken token = BeginPass();
        SuggestionScope scope = ResolveScope(requestedMode, isExplicitSession);
        Interlocked.Increment(ref _passesInFlight);
        _ = RunPassAsync(scope, requestedMode, isExplicitSession, isTypingTriggered, token);
    }

    /// <summary>
    /// Whether a queued pass has not yet published its outcome - including one still sitting in the
    /// debounce delay, which is the case this exists for.
    /// </summary>
    /// <remarks>
    /// <strong>PR #293 review, non-blocking 1.</strong> <c>Escape</c> used to be a no-op whenever the
    /// view-model said nothing was visible, which is exactly the state a keystroke leaves behind for the
    /// 75 ms of the debounce: press Escape inside that window and the pass landed anyway, so the bubble
    /// the user had just declined appeared. The controller asks this so it can cancel the pass and take
    /// the suppression flag - while still returning "not handled", because a key the user pressed at a
    /// prompt with nothing on screen belongs to the shell.
    /// </remarks>
    public bool HasPassInFlight => Volatile.Read(ref _passesInFlight) > 0;

    /// <summary>
    /// Reads the live command line, or returns <see langword="null"/> when there is nothing
    /// truthful to read.
    /// </summary>
    /// <remarks>
    /// Two conditions, and both are necessary. The lifecycle gate says the shell is in its line
    /// editor; the provider says the grid can actually be walked from the mark to the cursor. Either
    /// one alone is insufficient - the gate can be open while the mark has aged out of scrollback,
    /// and the provider is happy to walk a mark that describes a command that finished running two
    /// minutes ago.
    /// </remarks>
    public AssistQuerySnapshot? TryReadQuery()
    {
        if (!_context.IsAcceptingCommandInput || _context.IsAltScreenActive)
        {
            return null;
        }

        try
        {
            return _queryProvider();
        }
        catch
        {
            // The provider reaches across into the terminal buffer. A read that throws is a read
            // that produced no truth, which is the same answer as no mark at all.
            return null;
        }
    }

    /// <summary>
    /// Cancels the pass in flight, if any, so its result is never applied. Used wherever the surface
    /// is torn down or replaced by content the ranking pass must not overwrite.
    /// </summary>
    public void CancelPending()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _refreshCts, null);
        Cancel(previous);
    }

    private CancellationToken BeginPass()
    {
        var next = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _refreshCts, next);
        Cancel(previous);
        return next.Token;
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        // Deliberately not disposed. These sources never register a callback, never link to another
        // token and never arm a timer, so the only thing Dispose would reclaim is managed memory the
        // GC handles anyway - and skipping it removes any question about a pass still holding the
        // token when the source goes away.
        source?.Cancel();
    }

    private async Task RunPassAsync(
        SuggestionScope scope,
        CommandAssistMode requestedMode,
        bool isExplicitSession,
        bool isTypingTriggered,
        CancellationToken token)
    {
        try
        {
            await RunPassCoreAsync(scope, requestedMode, isExplicitSession, isTypingTriggered, token)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _passesInFlight);
        }
    }

    private async Task RunPassCoreAsync(
        SuggestionScope scope,
        CommandAssistMode requestedMode,
        bool isExplicitSession,
        bool isTypingTriggered,
        CancellationToken token)
    {
        // The query is resolved *inside* the worker, not here. Refresh() is called straight off the
        // keystroke, so anything before the Task.Run boundary still runs on the caller's thread -
        // which is exactly the mid-repaint read this design exists to avoid. Recorded outside the
        // lambda so the catch below and the outcome can both see whatever the pass managed to read.
        string query = string.Empty;

        // True until a snapshot says otherwise: see SuggestionRefreshOutcome.InsertionAppearsAvailable for
        // why "no snapshot" is not "no insertion" as far as the hint strip is concerned.
        bool insertionAppearsAvailable = true;

        if (isTypingTriggered && _passiveRefreshDebounce > TimeSpan.Zero)
        {
            try
            {
                await _delay(_passiveRefreshDebounce, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later keystroke. This is the whole point of the debounce and not a
                // fault: publishing anything here - even an empty outcome - would undo the coalescing.
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }
        }

        try
        {
            IReadOnlyList<AssistSuggestion> suggestions = await Task.Run(async () =>
            {
                // In a markless session this stays empty: degraded mode has no query, so an
                // explicit Search falls back to the recency list and everything prefix-dependent
                // finds nothing to work with. That is the intent, not a gap.
                //
                // TextBeforeCursor, not Text (PR #293 review, blocker 1). The reader returns the whole
                // painted line, and PSReadLine paints its inline prediction on that line to the right
                // of the cursor: ranking on Text meant ranking on the shell's guess, so typing `ec`
                // ranked on `echo <whatever history suggested>` and the bubble showed a row that had
                // nothing to do with the two characters the user had typed. Everything the floor below
                // and the stores see is now the text left of the cursor, which is the part the user put
                // there.
                AssistQuerySnapshot? snapshot = TryReadQuery();
                query = snapshot?.TextBeforeCursor ?? string.Empty;
                insertionAppearsAvailable = snapshot?.IsUsableAsTypedPrefix ?? true;

                // The >= 2 characters rule. Checked after the read and before any store or engine
                // work, so a one-character line costs a grid read and nothing else - and publishes an
                // empty outcome rather than returning, because a bubble ranked from three characters
                // has to disappear when the user backspaces down to one.
                //
                // It measures `query`, which is now the text left of the cursor, so a long inline
                // prediction no longer games the floor into letting a one-character line through.
                if (IsBelowPassiveQueryFloor(requestedMode, isExplicitSession, query))
                {
                    return (IReadOnlyList<AssistSuggestion>)Array.Empty<AssistSuggestion>();
                }

                var queryContext = new CommandAssistQueryContext(
                    query,
                    _context.WorkingDirectory,
                    _context.ShellKind,
                    _context.ProfileId,
                    _context.IsRemote,
                    IncludeHistorySuggestions: scope.IncludeHistory,
                    IncludeSnippetSuggestions: scope.IncludeSnippets,
                    IncludePathSuggestions: scope.IncludePaths,
                    HostId: _context.HostId);

                IReadOnlyList<CommandHistoryEntry> history = Array.Empty<CommandHistoryEntry>();
                if (queryContext.IncludeHistorySuggestions)
                {
                    history = string.IsNullOrWhiteSpace(query)
                        ? await _historyStore.GetRecentAsync(HistoryCandidatePoolSize).ConfigureAwait(false)
                        : await _historyStore.SearchAsync(query, HistoryCandidatePoolSize).ConfigureAwait(false);
                }

                IReadOnlyList<CommandSnippet> snippets = Array.Empty<CommandSnippet>();
                if (queryContext.IncludeSnippetSuggestions && _snippetStore != null)
                {
                    snippets = await _snippetStore.GetAllAsync().ConfigureAwait(false);
                }

                return _suggestionEngine.GetSuggestions(history, snippets, queryContext, MaxDisplayedSuggestions);
            }).ConfigureAwait(false);

            Publish(
                new SuggestionRefreshOutcome(
                    requestedMode,
                    query,
                    suggestions,
                    Faulted: false,
                    InsertionAppearsAvailable: insertionAppearsAvailable),
                token);
        }
        catch
        {
            Publish(
                new SuggestionRefreshOutcome(
                    requestedMode,
                    query,
                    Array.Empty<AssistSuggestion>(),
                    Faulted: true,
                    InsertionAppearsAvailable: insertionAppearsAvailable),
                token);
        }
    }

    private void Publish(SuggestionRefreshOutcome outcome, CancellationToken token)
    {
        // Checked before the dispatch as well as inside it. The inner check is what keeps a
        // superseded outcome off the surface, and it has to stay - the pass can be cancelled
        // between the two. The outer one is about not calling _dispatch at all: a pass whose
        // owner is already gone has nothing to publish, and handing it to a dispatch delegate
        // anyway is how a dead pass ends up touching live UI machinery (#81).
        if (token.IsCancellationRequested)
        {
            return;
        }

        _dispatch(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            _applyOutcome(outcome);
        });
    }

    /// <summary>
    /// Whether a typing-triggered passive pass has too little to work with to justify a surface.
    /// </summary>
    private static bool IsBelowPassiveQueryFloor(
        CommandAssistMode requestedMode,
        bool isExplicitSession,
        string query)
    {
        return requestedMode == CommandAssistMode.Suggest &&
               !isExplicitSession &&
               query.Length < MinPassiveQueryLength;
    }

    /// <summary>
    /// Which suggestion sources the current session is allowed to draw on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// History search is history-only. An explicit Suggest session gets everything. Help and Fix rank
    /// nothing.
    /// </para>
    /// <para>
    /// <strong>The passive Suggest scope is a deliberate policy reversal (V2 Phase 3b, task 1).</strong>
    /// It used to be paths-only, and the argument in this file was "unasked-for history rows were the
    /// noisiest part of V1". That is the M4.3 quiet-by-default policy, and the V2 design doc reverses
    /// it on purpose: paths-only means the bubble is silent in every session where the user is typing a
    /// command rather than a filename, which is most of them, and a feature that is invisible until you
    /// already know its shortcuts is the "visible usefulness" problem Phase 3 exists to fix. So the
    /// passive bubble now ranks history and paths together and shows the top row of the merged list.
    /// </para>
    /// <para>
    /// The noise argument was not wrong, it was answered elsewhere: <see cref="MinPassiveQueryLength"/>
    /// stops the bubble appearing on a barely-touched line, the debounce stops it flickering per
    /// keystroke, <c>Escape</c> takes it down for the rest of the command, and
    /// <see cref="AssistSessionContext.IsPassiveBubbleEnabled"/> is the kill switch that puts the passive
    /// scope back to paths-only for a user who disagrees.
    /// </para>
    /// <para>
    /// <strong>"Paths-only" is the scope, not the whole of M4.3 (PR #293 review, non-blocking 2).</strong>
    /// The switch narrows what a passive pass may draw on; it does not undo the other Phase 3b policies
    /// that apply to the passive path. <see cref="MinPassiveQueryLength"/> in particular still holds, so a
    /// one-character line offers no path completions either, where M4.3 would have. Exempting the floor
    /// when only paths are in scope was considered and not taken: a single character is not a path
    /// fragment worth ranking, and making the floor depend on the resolved scope would mean the floor and
    /// the scope could disagree about which pass they belong to. The switch is a scope control and this is
    /// what it says it is.
    /// </para>
    /// <para>
    /// Snippets stay out of the passive scope. A pinned snippet is a row the user built by hand, and
    /// putting it in a one-row bubble competing with a ranked history match makes the bubble's content
    /// unpredictable for no gain - the popup they pinned it for is one <c>Down</c> away.
    /// </para>
    /// <para>
    /// History is additionally gated on <see cref="AssistSessionContext.IsHistoryEnabled"/> in every
    /// scope (Phase 3b task 3): with history off there is nothing to recall, and the rest of the
    /// feature - paths, Help, Fix - keeps working, which is the whole point of decoupling the flags.
    /// </para>
    /// <para>
    /// Scope is a question about the session, not about the query, so a markless session resolves the
    /// same scopes as an instrumented one. What differs is what the sources do with an empty query:
    /// history returns the recency list (which is what makes explicit <c>Ctrl+R</c> still worth
    /// opening in a degraded session) and the path provider returns nothing, because it needs a
    /// command token and a path-shaped fragment to work from. Degraded passive suggestions are
    /// therefore empty by construction rather than by a special case - a markless session has no
    /// query, so it never clears <see cref="MinPassiveQueryLength"/> either.
    /// </para>
    /// </remarks>
    private SuggestionScope ResolveScope(CommandAssistMode requestedMode, bool isExplicitSession)
    {
        bool history = _context.IsHistoryEnabled;

        if (requestedMode == CommandAssistMode.Search)
        {
            return new SuggestionScope(
                IncludeHistory: history,
                IncludeSnippets: false,
                IncludePaths: false);
        }

        if (requestedMode == CommandAssistMode.Suggest && isExplicitSession)
        {
            return new SuggestionScope(
                IncludeHistory: history,
                IncludeSnippets: true,
                IncludePaths: true);
        }

        if (requestedMode == CommandAssistMode.Suggest)
        {
            return new SuggestionScope(
                IncludeHistory: history && _context.IsPassiveBubbleEnabled,
                IncludeSnippets: false,
                IncludePaths: true);
        }

        return new SuggestionScope(
            IncludeHistory: false,
            IncludeSnippets: false,
            IncludePaths: false);
    }

    private readonly record struct SuggestionScope(
        bool IncludeHistory,
        bool IncludeSnippets,
        bool IncludePaths);
}
