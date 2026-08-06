using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

public sealed class CommandAssistModeRouter
{
    private const double FixModeThreshold = 0.8;

    /// <summary>
    /// The lowest confidence anything in the recogniser table publishes, and a defensive lower bound
    /// on what may put a bubble on screen unasked.
    /// </summary>
    /// <remarks>
    /// Belt and braces rather than the actual gate - see
    /// <see cref="ShouldSurfacePassiveFix"/>, where provenance does the work. This exists so that a
    /// future recogniser publishing below the table's own weakest rung cannot surface by accident.
    /// </remarks>
    public const double PassiveFixConfidenceFloor = CommandErrorRecognizers.Explanatory;

    public CommandAssistMode ChooseModeForHelpRequest()
    {
        return CommandAssistMode.Help;
    }

    public CommandAssistMode ChooseModeForFailure(double highestConfidence)
    {
        return highestConfidence >= FixModeThreshold
            ? CommandAssistMode.Fix
            : CommandAssistMode.Suggest;
    }

    /// <summary>
    /// Whether a failure's insights are worth showing in a bubble the user did not ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The UX-polish round's noise floor.</strong> The owner's complaint was "fix comes
    /// sometimes when it is not needed", and the code agreed with him: <c>HandleCommandFailureAsync</c>
    /// surfaced a bubble for <em>any</em> non-empty result list, so every non-zero exit in the session
    /// produced one. His screenshot was a failing <c>print -l $precmd_functions</c> - a zsh builtin
    /// that ran, printed something no recogniser models, and exited non-zero - answered with a
    /// name-similarity guess, which is the ladder's bottom rung doing exactly what its own
    /// documentation says it should not be trusted to do.
    /// </para>
    /// <para>
    /// <strong>The gate is provenance, not confidence.</strong> A row may auto-surface if and only if
    /// a recogniser in the table produced it: something read the output tail and recognised what it
    /// said. Everything the owner complained about falls out of that one rule, because every one of
    /// those rows came from <c>HeuristicErrorInsightService</c>'s fallback rather than the table - the
    /// "output matched nothing" correction at 0.40, and the uninformed no-output guess at 0.55/0.70.
    /// </para>
    /// <para>
    /// <strong>Why not a confidence threshold as well.</strong> The obvious version of this fix was
    /// "confidence >= <see cref="CommandErrorRecognizers.Plausible"/>", and it was written that way
    /// first. It is wrong in both directions. Too permissive, because the uninformed fallback is
    /// priced at <see cref="CommandErrorRecognizers.Likely"/> (0.70) - deliberately, since with no
    /// output to read a name-similarity guess is the best answer available, but "best available" is
    /// not "worth interrupting for", and clearing a bar is not the same as having read anything. Too
    /// strict, because <see cref="CommandErrorRecognizers.Explanatory"/> (0.40) is what the table
    /// itself uses for a recognised failure that has no single command to run - and
    /// <c>git status</c> outside a working tree, answered with "This directory is not inside a Git
    /// repository", is exactly the case this feature exists for. Confidence measures how sure a
    /// recogniser is of its remedy; it was never a measure of whether anything was understood.
    /// </para>
    /// <para>
    /// Confidence still decides whether the <em>popup</em> opens over the user's next command - see
    /// <see cref="ChooseModeForFailure"/> - which is the interruption that actually costs something.
    /// </para>
    /// <para>
    /// <strong>What suppression costs, and why it is affordable.</strong> Nothing is discarded - the
    /// insights are still computed and still available on demand, because Fix is reachable by
    /// <c>Ctrl+Space</c> after a failure the same as before. The user who wants a guess can ask for
    /// one; what they no longer get is a guess volunteered over every failing command they run. A
    /// suggestion surface that is wrong a third of the time trains people to ignore it, and then it is
    /// worth nothing on the occasion it is right.
    /// </para>
    /// </remarks>
    public bool ShouldSurfacePassiveFix(IReadOnlyList<CommandFixSuggestion> fixes)
    {
        if (fixes is null || fixes.Count == 0)
        {
            return false;
        }

        return fixes.Any(item => item.IsRecognized && item.Confidence >= PassiveFixConfidenceFloor);
    }
}
