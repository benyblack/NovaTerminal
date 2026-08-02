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
/// <param name="Query">
/// The query the pass actually ranked. Carried back rather than read from the view-model because as
/// of Phase 1c the pass resolves its own query - it reads the grid when the pass runs, not when the
/// keystroke that triggered it arrived - so the controller has no other way to know what the rows on
/// screen are rows *for*. This is what the view-model's <c>QueryText</c> is set from.
/// </param>
/// <param name="Suggestions">The ranked rows; empty when the pass failed.</param>
/// <param name="Faulted">
/// True when the candidate fetch or the ranking threw. The surface is cleared rather than left
/// showing rows from a previous query.
/// </param>
internal readonly record struct SuggestionRefreshOutcome(
    CommandAssistMode RequestedMode,
    string Query,
    IReadOnlyList<AssistSuggestion> Suggestions,
    bool Faulted);
