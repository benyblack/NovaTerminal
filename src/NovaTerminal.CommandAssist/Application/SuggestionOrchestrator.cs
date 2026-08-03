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
/// keystrokes arrive together only the last pass's read is applied. That is coalescing by
/// supersession, not by timing: there is deliberately no debounce here, because a debounce is a
/// policy decision and Phase 3 owns it.
/// </para>
/// <para>
/// The remaining window is honest and small: a pass whose read beats the shell's echo of the last
/// character ranks a one-character-stale query until the next trigger. Phase 3's debounce closes it.
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

    private readonly IHistoryStore _historyStore;
    private readonly ISnippetStore? _snippetStore;
    private readonly ISuggestionEngine _suggestionEngine;
    private readonly AssistSessionContext _context;
    private readonly Func<AssistQuerySnapshot?> _queryProvider;
    private readonly Action<Action> _dispatch;
    private readonly Action<SuggestionRefreshOutcome> _applyOutcome;

    private CancellationTokenSource? _refreshCts;

    public SuggestionOrchestrator(
        IHistoryStore historyStore,
        ISnippetStore? snippetStore,
        ISuggestionEngine suggestionEngine,
        AssistSessionContext context,
        Func<AssistQuerySnapshot?> queryProvider,
        Action<Action> dispatch,
        Action<SuggestionRefreshOutcome> applyOutcome)
    {
        _historyStore = historyStore;
        _snippetStore = snippetStore;
        _suggestionEngine = suggestionEngine;
        _context = context;
        _queryProvider = queryProvider;
        _dispatch = dispatch;
        _applyOutcome = applyOutcome;
    }

    /// <summary>
    /// Queues a ranking pass, superseding any pass still in flight. Returns immediately; the pass
    /// resolves its own query and the outcome arrives through the dispatcher.
    /// </summary>
    public void Refresh(CommandAssistMode requestedMode, bool isExplicitSession)
    {
        CancellationToken token = BeginPass();
        SuggestionScope scope = ResolveScope(requestedMode, isExplicitSession);
        _ = RunPassAsync(scope, requestedMode, token);
    }

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
        CancellationToken token)
    {
        // The query is resolved *inside* the worker, not here. Refresh() is called straight off the
        // keystroke, so anything before the Task.Run boundary still runs on the caller's thread -
        // which is exactly the mid-repaint read this design exists to avoid. Recorded outside the
        // lambda so the catch below and the outcome can both see whatever the pass managed to read.
        string query = string.Empty;

        try
        {
            IReadOnlyList<AssistSuggestion> suggestions = await Task.Run(async () =>
            {
                // In a markless session this stays empty: degraded mode has no query, so an
                // explicit Search falls back to the recency list and everything prefix-dependent
                // finds nothing to work with. That is the intent, not a gap.
                query = TryReadQuery()?.Text ?? string.Empty;

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

            Publish(new SuggestionRefreshOutcome(requestedMode, query, suggestions, Faulted: false), token);
        }
        catch
        {
            Publish(
                new SuggestionRefreshOutcome(requestedMode, query, Array.Empty<AssistSuggestion>(), Faulted: true),
                token);
        }
    }

    private void Publish(SuggestionRefreshOutcome outcome, CancellationToken token)
    {
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
    /// Which suggestion sources the current session is allowed to draw on.
    /// </summary>
    /// <remarks>
    /// History search is history-only. An explicit Suggest session gets everything. A passive Suggest
    /// bubble - one the user never asked for - gets paths only: unasked-for history rows were the
    /// noisiest part of V1. Help and Fix rank nothing.
    /// <para>
    /// Scope is a question about the session, not about the query, so a markless session resolves the
    /// same scopes as an instrumented one. What differs is what the sources do with an empty query:
    /// history returns the recency list (which is what makes explicit <c>Ctrl+R</c> still worth
    /// opening in a degraded session) and the path provider returns nothing, because it needs a
    /// command token and a path-shaped fragment to work from. Degraded passive suggestions are
    /// therefore empty by construction rather than by a special case.
    /// </para>
    /// </remarks>
    private static SuggestionScope ResolveScope(CommandAssistMode requestedMode, bool isExplicitSession)
    {
        if (requestedMode == CommandAssistMode.Search)
        {
            return new SuggestionScope(
                IncludeHistory: true,
                IncludeSnippets: false,
                IncludePaths: false);
        }

        if (requestedMode == CommandAssistMode.Suggest && isExplicitSession)
        {
            return new SuggestionScope(
                IncludeHistory: true,
                IncludeSnippets: true,
                IncludePaths: true);
        }

        if (requestedMode == CommandAssistMode.Suggest)
        {
            return new SuggestionScope(
                IncludeHistory: false,
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
