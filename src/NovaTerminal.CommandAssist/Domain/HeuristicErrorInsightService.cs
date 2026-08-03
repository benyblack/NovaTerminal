using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// Fix mode's local brain: a table of recognisers run over the failing command and the tail of what
/// it printed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What changed in V2 Phase 4a.</strong> This class used to have three branches, two of
/// which were unreachable: both keyed off <c>CommandFailureContext.ErrorOutput</c>, and the single
/// call site in <c>TerminalPane</c> passed a hard-coded <c>null</c>. The one branch that did run
/// was an edit-distance guess against a seven-name list, published at 0.82 against a Fix threshold
/// of 0.8 - so the only thing Fix mode could ever say was "did you mean git?", and it said it on
/// the strength of no evidence about what had actually gone wrong. Phase 4a task 1 fills
/// <see cref="CommandFailureContext.OutputTail"/> from the grid, which is what makes the rest of
/// the table reachable at all.
/// </para>
/// <para>
/// <strong>The dispatch is deliberately dumb.</strong> Every recogniser is asked, in table order,
/// and the results are concatenated. There is no early exit on a match: a command-not-found on a
/// script file legitimately produces both "did you mean" and "run it with ./", and a first-match
/// rule would have to encode a priority that the confidence numbers already express better. The
/// cost is bounded - fifteen substring scans over at most 8 KB, once per failing command.
/// </para>
/// <para>
/// <strong>The output-tail ladder.</strong> How much the service is willing to infer scales with
/// how much it can see:
/// <list type="number">
/// <item><description><em>Output captured and a recogniser matched</em> - the table's answer,
/// at the confidence the recogniser chose.</description></item>
/// <item><description><em>Output captured, nothing matched</em> - a typo correction is
/// <em>refused</em> above <see cref="CommandErrorRecognizers.Plausible"/>. The command ran, printed
/// something we do not understand, and failed; "did you mean git?" for a <c>git</c> that exists and
/// exited 1 is noise. This is the case the old 0.82 branch got wrong.</description></item>
/// <item><description><em>No output captured at all</em> (markless session, scrolled past,
/// stale mark) - the pre-Phase-4a behaviour survives unchanged, because it is all there is: a
/// correction of the first token, capped below the Fix threshold so it informs without
/// interrupting.</description></item>
/// </list>
/// </para>
/// <para>
/// Ordering is by confidence, then by title, so the surface's top row is the strongest claim and
/// the order is stable across runs (the popup's selected index is 0 and must not move between two
/// equally confident rows).
/// </para>
/// </remarks>
public sealed class HeuristicErrorInsightService : IErrorInsightService
{
    private readonly IReadOnlyList<CommandErrorRecognizer> _recognizers;

    public HeuristicErrorInsightService()
        : this(CommandErrorRecognizers.All)
    {
    }

    /// <summary>
    /// Test seam: run a subset of the table. Production always uses
    /// <see cref="CommandErrorRecognizers.All"/>.
    /// </summary>
    public HeuristicErrorInsightService(IReadOnlyList<CommandErrorRecognizer> recognizers)
    {
        _recognizers = recognizers ?? throw new ArgumentNullException(nameof(recognizers));
    }

    public Task<IReadOnlyList<CommandFixSuggestion>> AnalyzeAsync(
        CommandFailureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null || string.IsNullOrWhiteSpace(context.CommandText))
        {
            return Empty;
        }

        var signal = new CommandErrorSignal(context);
        if (signal.PrimaryToken.Length == 0)
        {
            return Empty;
        }

        List<CommandFixSuggestion> suggestions = [];
        foreach (CommandErrorRecognizer recognizer in _recognizers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CommandFixSuggestion> matched = recognizer.Analyze(signal);
            if (matched.Count > 0)
            {
                suggestions.AddRange(matched);
            }
        }

        if (suggestions.Count == 0)
        {
            CommandFixSuggestion? fallback = TryBuildUninformedCorrection(signal);
            if (fallback != null)
            {
                suggestions.Add(fallback);
            }
        }

        IReadOnlyList<CommandFixSuggestion> ordered = suggestions
            .GroupBy(item => item.SuggestedCommand, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(ordered);
    }

    /// <summary>
    /// The bottom two rungs of the ladder in the class remarks: a typo correction offered on
    /// weaker evidence than a command-not-found message, and priced accordingly.
    /// </summary>
    /// <remarks>
    /// The distinction between "the command printed nothing we recognise" and "we could not read
    /// what it printed" is the whole content of this method, and it is why
    /// <see cref="CommandErrorSignal.HasOutput"/> exists. With no output the service is in exactly
    /// the position V1 was permanently in, and the old answer - a capped correction - is still the
    /// best available. With output that matched nothing, the same answer would be actively worse
    /// than silence, so it is priced below the point where it can open a popup and below the point
    /// where it reads as a claim.
    /// </remarks>
    private static CommandFixSuggestion? TryBuildUninformedCorrection(CommandErrorSignal signal)
    {
        if (signal.ExitCode is null or 0)
        {
            return null;
        }

        string? corrected = FixKnownCommands.TryCorrect(signal.PrimaryToken, out int distance);
        if (corrected == null)
        {
            return null;
        }

        double confidence = signal.HasOutput
            ? CommandErrorRecognizers.Explanatory
            : distance == 1
                ? CommandErrorRecognizers.Likely
                : CommandErrorRecognizers.Plausible;

        return new CommandFixSuggestion(
            Title: $"Did you mean {corrected}?",
            SuggestedCommand: signal.WithPrimaryToken(corrected),
            Description: signal.HasOutput
                ? "The command failed for a reason this terminal does not recognise; "
                    + $"'{corrected}' is the closest known command name."
                : "No output was captured for this command, so this is a name-similarity guess only.",
            Confidence: confidence,
            Badges: ["Fix", "Typo"]);
    }

    private static Task<IReadOnlyList<CommandFixSuggestion>> Empty =>
        Task.FromResult<IReadOnlyList<CommandFixSuggestion>>(Array.Empty<CommandFixSuggestion>());
}
