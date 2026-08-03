using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

public sealed class CommandAssistSuggestionEngine : ISuggestionEngine
{
    /// <summary>
    /// What a same-context history entry is worth on the empty-query (<c>Ctrl+R</c>) path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Large enough to <em>partition</em> rather than nudge: every other empty-query term together
    /// tops out around 30 (recency is a tiebreak, not a score, and frequency is capped at 5), so this
    /// puts every entry from the current host - or every local entry, on a local pane - above every
    /// entry that is not, and orders within each band by the usual recency/frequency rules.
    /// </para>
    /// <para>
    /// <strong>Partitioning, not filtering.</strong> The owner's report was that <c>Ctrl+R</c> "shows
    /// commands from all sessions/tabs indiscriminately", and the fix is deliberately an ordering one:
    /// a command run on another host is still in the list, below the fold, because reaching for a
    /// command you remember running somewhere else is the reason a shared history exists. Hiding it
    /// would trade one complaint for a worse one.
    /// </para>
    /// </remarks>
    internal const double EmptyQueryContextMatchBoost = 1000;

    /// <summary>
    /// What a same-profile history entry is worth on the empty-query path: a secondary sort within
    /// each context band, not a band of its own.
    /// </summary>
    internal const double EmptyQueryProfileMatchBoost = 200;

    /// <summary>
    /// What an <em>unpinned</em> snippet is worth on the empty-query path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The PR #290 review's third blocker, decided explicitly.</strong> V2 Phase 3a gave
    /// same-context history a partitioning boost of <see cref="EmptyQueryContextMatchBoost"/>, which
    /// left an unpinned snippet scoring about 10 against every local history entry's 1001 - below
    /// commands the user ran once and never thought about again. A snippet is user-authored text someone
    /// deliberately saved; ranking it under ambient history is the wrong answer even if it is not the
    /// worst one. The dead <c>+8 discoverability</c> nudge that was supposed to prevent this is gone
    /// with this constant replacing it.
    /// </para>
    /// <para>
    /// The band chosen is the same-profile one (deliberately the same number as
    /// <see cref="EmptyQueryProfileMatchBoost"/>, not a coincidence): <em>above</em> cross-context
    /// history, <em>below</em> the commands this very host ran. A snippet carries no host and no
    /// profile, so it cannot claim to be from "here"; what it can claim is that a human wrote it down
    /// on purpose, which is worth more than another machine's recency. A pinned snippet is unaffected -
    /// pinning already satisfies both affinity terms below, which puts it in the top band, which is the
    /// whole point of pinning.
    /// </para>
    /// <para>
    /// The honest cost: with more same-context history entries than the display cap, an unpinned snippet
    /// is below the fold. Pinning is the answer to that and is one keystroke
    /// (<c>Ctrl+Shift+P</c>); hoisting every snippet above this host's own history would make the
    /// empty-query list say "snippets first" for users who never asked for that.
    /// </para>
    /// </remarks>
    internal const double EmptyQueryUnpinnedSnippetBandBoost = EmptyQueryProfileMatchBoost;

    /// <summary>
    /// What a same-context history entry is worth once the user has typed something.
    /// </summary>
    /// <remarks>
    /// Deliberately a nudge here rather than a partition. With a query on the line the user has said
    /// what they are looking for, and the text-match terms (prefix 120, token prefix 70, contains 25)
    /// are the better signal; a partition would rank a same-host subsequence match above a local
    /// prefix match, which reads as the list ignoring what was typed. Sized to sit between the
    /// existing cwd (12) and profile (20) signals and a text-match tier.
    /// </remarks>
    internal const double TextQueryContextMatchBoost = 30;

    private readonly IPathSuggestionProvider _pathSuggestionProvider;

    public CommandAssistSuggestionEngine(IPathSuggestionProvider? pathSuggestionProvider = null)
    {
        _pathSuggestionProvider = pathSuggestionProvider ?? new FileSystemPathSuggestionProvider();
    }

    public IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> historyEntries,
        CommandAssistQueryContext context,
        int maxResults)
    {
        return GetSuggestions(historyEntries, Array.Empty<CommandSnippet>(), context, maxResults);
    }

    public IReadOnlyList<AssistSuggestion> GetSuggestions(
        IReadOnlyList<CommandHistoryEntry> historyEntries,
        IReadOnlyList<CommandSnippet> snippets,
        CommandAssistQueryContext context,
        int maxResults)
    {
        if (maxResults <= 0)
        {
            return Array.Empty<AssistSuggestion>();
        }

        string query = context.Input?.Trim() ?? string.Empty;
        List<AssistSuggestion> results = new();
        if (context.IncludePathSuggestions)
        {
            results.AddRange(_pathSuggestionProvider.GetSuggestions(context, maxResults));
        }

        if (context.IncludeHistorySuggestions)
        {
            results.AddRange(BuildHistorySuggestions(historyEntries, context, query));
        }

        if (context.IncludeSnippetSuggestions)
        {
            results.AddRange(BuildSnippetSuggestions(snippets, context, query));
        }
        bool hasPathSuggestions = results.Any(x => x.Type == AssistSuggestionType.Path);

        return results
            .OrderByDescending(x => ComputeEffectiveScore(x, hasPathSuggestions))
            .ThenByDescending(x => x.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    private static double ComputeEffectiveScore(AssistSuggestion suggestion, bool hasPathSuggestions)
    {
        if (hasPathSuggestions && suggestion.Type == AssistSuggestionType.Path)
        {
            return suggestion.Score + 1000;
        }

        return suggestion.Score;
    }

    private static IEnumerable<AssistSuggestion> BuildHistorySuggestions(
        IReadOnlyList<CommandHistoryEntry> historyEntries,
        CommandAssistQueryContext context,
        string query)
    {
        return historyEntries
            .GroupBy(x => x.CommandText, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                CommandHistoryEntry latest = group.OrderByDescending(x => x.ExecutedAt).First();
                int frequency = group.Count();
                double score = ScoreText(
                    latest.CommandText,
                    query,
                    latest.WorkingDirectory,
                    latest.ShellKind,
                    latest.ExitCode,
                    context,
                    frequency,
                    isPinned: false,
                    isContextMatch: IsSameSessionContext(latest, context),
                    isProfileMatch: Matches(context.ProfileId, latest.ProfileId));

                return new
                {
                    Latest = latest,
                    Frequency = frequency,
                    Score = score
                };
            })
            .Where(x => x.Score > 0)
            .Select(x => new AssistSuggestion(
                Id: x.Latest.Id,
                Type: AssistSuggestionType.History,
                DisplayText: x.Latest.CommandText,
                InsertText: x.Latest.CommandText,
                Description: null,
                Badges: BuildHistoryBadges(x.Latest, context, x.Frequency),
                Score: x.Score,
                WorkingDirectory: x.Latest.WorkingDirectory,
                LastUsedAt: x.Latest.ExecutedAt,
                ExitCode: x.Latest.ExitCode));
    }

    private static IEnumerable<AssistSuggestion> BuildSnippetSuggestions(
        IReadOnlyList<CommandSnippet> snippets,
        CommandAssistQueryContext context,
        string query)
    {
        return snippets
            .Select(snippet =>
            {
                double score = ScoreText(
                    snippet.CommandText,
                    query,
                    snippet.WorkingDirectory,
                    snippet.ShellKind,
                    exitCode: 0,
                    context,
                    frequency: 1,
                    isPinned: snippet.IsPinned,

                    // A pinned snippet is in scope for every session by definition - that is what
                    // pinning means - so it satisfies both affinity terms rather than being pushed
                    // below every same-host history entry by a scoping rule it has no fields to
                    // satisfy (a snippet carries no host and no profile). Without this, V2 Phase 3a's
                    // context boost would have silently demoted pinned snippets out of the top of an
                    // empty-query list, which is the one place users put things to find them.
                    // An unpinned snippet gets the empty-query band below instead.
                    //
                    // Side effect worth naming (PR #290 review): on the *text*-query path these two
                    // flags are also what a pinned snippet's profileScore (20) and contextScore (30) are
                    // computed from, so pinning is worth +50 there on top of its own pinScore (40) - a
                    // pinned snippet outranks an equally-matching history entry by 90 rather than 40.
                    // Deliberate: the boosts are affinity terms and a pinned snippet is in scope
                    // everywhere by definition. Documented rather than tuned, because the text-query
                    // tiers (prefix 120, token 70, contains 25) still dominate, so what the user typed
                    // still decides the order.
                    isContextMatch: snippet.IsPinned,
                    isProfileMatch: snippet.IsPinned);

                if (string.IsNullOrWhiteSpace(query) && !snippet.IsPinned)
                {
                    score += EmptyQueryUnpinnedSnippetBandBoost;
                }

                return new
                {
                    Snippet = snippet,
                    Score = score
                };
            })
            .Where(x => x.Score > 0)
            .Select(x => new AssistSuggestion(
                Id: x.Snippet.Id,
                Type: AssistSuggestionType.Snippet,
                DisplayText: x.Snippet.Name,
                InsertText: x.Snippet.CommandText,
                Description: x.Snippet.Description,
                Badges: BuildSnippetBadges(x.Snippet, context),
                Score: x.Score,
                WorkingDirectory: x.Snippet.WorkingDirectory,
                LastUsedAt: x.Snippet.LastUsedAt ?? x.Snippet.CreatedAt,
                ExitCode: null));
    }

    private static double ScoreText(
        string text,
        string query,
        string? workingDirectory,
        string? shellKind,
        int? exitCode,
        CommandAssistQueryContext context,
        int frequency,
        bool isPinned,
        bool isContextMatch,
        bool isProfileMatch)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            // The empty-query (Ctrl+R) path. Recency is the tiebreak rather than a term here - see the
            // ThenByDescending in GetSuggestions - so the context boosts are the only thing large
            // enough to reorder the list, which is exactly what V2 Phase 3a wants them to do.
            return 1 +
                   Math.Min(frequency, 5) +
                   (isPinned ? 20 : 0) +
                   (isContextMatch ? EmptyQueryContextMatchBoost : 0) +
                   (isProfileMatch ? EmptyQueryProfileMatchBoost : 0);
        }

        string normalizedText = text.Trim();
        string normalizedQuery = query.Trim();
        string lowerText = normalizedText.ToLowerInvariant();
        string lowerQuery = normalizedQuery.ToLowerInvariant();

        double prefixScore = lowerText.StartsWith(lowerQuery, StringComparison.Ordinal) ? 120 : 0;
        double tokenPrefixScore = lowerText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.StartsWith(lowerQuery, StringComparison.Ordinal))
            ? 70
            : 0;
        double containsScore = lowerText.Contains(lowerQuery, StringComparison.Ordinal) ? 25 : 0;
        double subsequenceScore = IsSubsequence(lowerQuery, lowerText) ? 12 : 0;
        double textMatchScore = prefixScore + tokenPrefixScore + containsScore + subsequenceScore;
        if (textMatchScore <= 0)
        {
            return 0;
        }

        double frequencyScore = frequency * 4;
        double cwdScore = Matches(context.WorkingDirectory, workingDirectory) ? 12 : 0;
        double shellScore = Matches(context.ShellKind, shellKind) ? 4 : 0;
        double profileScore = isProfileMatch ? 20 : 0;
        double contextScore = isContextMatch ? TextQueryContextMatchBoost : 0;
        double successScore = exitCode == 0 ? 18 : exitCode.HasValue ? -8 : 0;
        double pinScore = isPinned ? 40 : 0;

        return textMatchScore + frequencyScore + cwdScore + shellScore + profileScore + contextScore + successScore + pinScore;
    }

    /// <summary>
    /// Whether a history entry came from the same place the current pane is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two cases, and the asymmetry is deliberate. On a remote pane, "here" means <em>this host</em>:
    /// the commands that make sense are the ones that ran on the box, and a host id is the only thing
    /// that identifies it across sessions, tabs and reconnects (a session id would scope the list to
    /// this tab, which is what a shell's own per-session history already does badly). On a local pane,
    /// "here" means "not on somebody else's machine" - there is no local host id to compare, and
    /// `ubuntu.example`'s `apt install` has no business at the top of a Windows prompt.
    /// </para>
    /// <para>
    /// A remote pane with no host id (an SSH profile with the host still unresolved) matches nothing
    /// rather than matching everything: an unknown context is not a context, and the fallback is the
    /// pre-Phase-3a pure-recency order, which is merely unhelpful rather than wrong.
    /// </para>
    /// </remarks>
    private static bool IsSameSessionContext(CommandHistoryEntry entry, CommandAssistQueryContext context)
    {
        if (!context.IsRemote)
        {
            return !entry.IsRemote;
        }

        return entry.IsRemote && Matches(context.HostId, entry.HostId);
    }

    private static IReadOnlyList<string> BuildHistoryBadges(CommandHistoryEntry entry, CommandAssistQueryContext context, int frequency)
    {
        List<string> badges = new();
        if (frequency > 1)
        {
            badges.Add("Frequent");
        }

        if (Matches(context.WorkingDirectory, entry.WorkingDirectory))
        {
            badges.Add("Same cwd");
        }

        if (entry.ExitCode == 0)
        {
            badges.Add("Worked");
        }

        return badges;
    }

    private static IReadOnlyList<string> BuildSnippetBadges(CommandSnippet snippet, CommandAssistQueryContext context)
    {
        List<string> badges = new() { "Snippet" };
        if (snippet.IsPinned)
        {
            badges.Add("Pinned");
        }

        if (Matches(context.WorkingDirectory, snippet.WorkingDirectory))
        {
            badges.Add("Same cwd");
        }

        return badges;
    }

    private static bool Matches(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubsequence(string query, string text)
    {
        int index = 0;
        for (int i = 0; i < text.Length && index < query.Length; i++)
        {
            if (text[i] == query[index])
            {
                index++;
            }
        }

        return index == query.Length;
    }
}
