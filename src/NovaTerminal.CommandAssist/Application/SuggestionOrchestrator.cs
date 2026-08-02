using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Turns "the query changed" into a ranked list: resolves which sources are in scope for the current
/// session, fetches candidates off the UI thread, ranks them with the one suggestion engine, and
/// hands the result back through the dispatcher - unless a newer pass has superseded it.
/// </summary>
/// <remarks>
/// <para>
/// Staleness is handled with a <see cref="CancellationTokenSource"/> per pass: queueing a refresh
/// cancels the one before it, and an outcome whose token is cancelled is dropped instead of applied.
/// This replaces the interlocked <c>_refreshVersion</c> counter that used to serve the same purpose;
/// the behavior is identical, but the mechanism is one Phase 3 can hang a debounce off and Phase 1
/// can thread into the stores. Note that the token is deliberately <em>not</em> passed to the store
/// or engine calls yet: cancelling work already in flight is a behavior change, and this pass is a
/// mechanism swap only.
/// </para>
/// <para>
/// There is no debounce here. Phase 3 owns that policy decision.
/// </para>
/// </remarks>
internal sealed class SuggestionOrchestrator
{
    /// <summary>Rows the popup/bubble shows. M4.2's hard-coded cap; Phase 3 replaces it with scrolling.</summary>
    public const int MaxDisplayedSuggestions = 5;

    /// <summary>
    /// How many history candidates the ranking engine gets to choose from for a text query.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="MaxDisplayedSuggestions"/> on purpose. The store used to pre-rank with
    /// its own scoring function and hand back its own top five, so the engine only ever re-ordered
    /// a set the store had already picked. Now the store is a recall gate that truncates by
    /// recency, so it has to hand over enough rows for the engine's cwd / shell / profile / exit-code
    /// signals to matter - otherwise a relevant-but-older command could never reach the list.
    /// Empty-query recall stays at the display cap: with no text to score, "most recent five" is
    /// already the answer, and widening it would let the frequency signal outrank recency.
    /// </remarks>
    private const int HistoryCandidatePoolSize = 50;

    private readonly IHistoryStore _historyStore;
    private readonly ISnippetStore? _snippetStore;
    private readonly ISuggestionEngine _suggestionEngine;
    private readonly AssistSessionContext _context;
    private readonly Action<Action> _dispatch;
    private readonly Action<SuggestionRefreshOutcome> _applyOutcome;

    private CancellationTokenSource? _refreshCts;

    public SuggestionOrchestrator(
        IHistoryStore historyStore,
        ISnippetStore? snippetStore,
        ISuggestionEngine suggestionEngine,
        AssistSessionContext context,
        Action<Action> dispatch,
        Action<SuggestionRefreshOutcome> applyOutcome)
    {
        _historyStore = historyStore;
        _snippetStore = snippetStore;
        _suggestionEngine = suggestionEngine;
        _context = context;
        _dispatch = dispatch;
        _applyOutcome = applyOutcome;
    }

    /// <summary>
    /// Queues a ranking pass for <paramref name="query"/>, superseding any pass still in flight.
    /// Returns immediately; the outcome arrives through the dispatcher.
    /// </summary>
    public void Refresh(CommandAssistMode requestedMode, bool isExplicitSession, string query)
    {
        CancellationToken token = BeginPass();
        SuggestionScope scope = ResolveScope(requestedMode, isExplicitSession);
        var queryContext = new CommandAssistQueryContext(
            query,
            _context.WorkingDirectory,
            _context.ShellKind,
            _context.ProfileId,
            _context.IsRemote,
            IncludeHistorySuggestions: scope.IncludeHistory,
            IncludeSnippetSuggestions: scope.IncludeSnippets,
            IncludePathSuggestions: scope.IncludePaths);

        _ = RunPassAsync(query, queryContext, requestedMode, token);
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
        string query,
        CommandAssistQueryContext queryContext,
        CommandAssistMode requestedMode,
        CancellationToken token)
    {
        try
        {
            IReadOnlyList<AssistSuggestion> suggestions = await Task.Run(async () =>
            {
                IReadOnlyList<CommandHistoryEntry> history = Array.Empty<CommandHistoryEntry>();
                if (queryContext.IncludeHistorySuggestions)
                {
                    history = string.IsNullOrWhiteSpace(query)
                        ? await _historyStore.GetRecentAsync(MaxDisplayedSuggestions).ConfigureAwait(false)
                        : await _historyStore.SearchAsync(query, HistoryCandidatePoolSize).ConfigureAwait(false);
                }

                IReadOnlyList<CommandSnippet> snippets = Array.Empty<CommandSnippet>();
                if (queryContext.IncludeSnippetSuggestions && _snippetStore != null)
                {
                    snippets = await _snippetStore.GetAllAsync().ConfigureAwait(false);
                }

                return _suggestionEngine.GetSuggestions(history, snippets, queryContext, MaxDisplayedSuggestions);
            }).ConfigureAwait(false);

            Publish(new SuggestionRefreshOutcome(requestedMode, suggestions, Faulted: false), token);
        }
        catch
        {
            Publish(
                new SuggestionRefreshOutcome(requestedMode, Array.Empty<AssistSuggestion>(), Faulted: true),
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
