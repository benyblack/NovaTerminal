using System.Collections.Generic;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// The result of one ranking pass, handed back to the controller on the dispatcher thread.
/// </summary>
/// <param name="RequestedMode">
/// The mode the pass was queued for. The controller drops the outcome if the session has moved on to
/// a different mode since - a Help popup must not be overwritten by a Suggest pass that was already
/// in flight when it opened.
/// </param>
/// <param name="Suggestions">The ranked rows; empty when the pass failed.</param>
/// <param name="Faulted">
/// True when the candidate fetch or the ranking threw. The surface is cleared rather than left
/// showing rows from a previous query.
/// </param>
public readonly record struct SuggestionRefreshOutcome(
    CommandAssistMode RequestedMode,
    IReadOnlyList<AssistSuggestion> Suggestions,
    bool Faulted);
