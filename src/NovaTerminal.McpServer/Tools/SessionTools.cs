using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using NovaTerminal.AgentHost.Contracts;

namespace NovaTerminal.McpServer.Tools;

/// <summary>
/// Live-session observe tools (docs/agent-host/DIRECTION.md, milestone A1).
/// These proxy the running app's local IPC endpoint via <see cref="AgentHostClient"/>;
/// they are read-only by protocol design and only work while the user has
/// enabled "Agent access (observe)" in NovaTerminal's settings.
/// </summary>
[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "novaterminal.list_sessions"),
     Description("Lists the live terminal sessions in the running NovaTerminal app: pane id, title, profile, kind (local/ssh), size, and active state. Requires NovaTerminal to be running with 'Agent access (observe)' enabled; returns instructions if it isn't. Use the paneId with novaterminal.read_screen / novaterminal.read_scrollback.")]
    public static async Task<string> ListSessions(AgentHostClient client, CancellationToken cancellationToken)
    {
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ListSessions, null, cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        return TryDeserializeResult(result, AgentHostJsonContext.Default.ListSessionsResult, out var dto)
            ? FormatSessionList(dto!.Sessions)
            : AgentHostClient.ProtocolErrorMessage;
    }

    [McpServerTool(Name = "novaterminal.read_screen"),
     Description("Reads the visible screen of a live NovaTerminal session as text: the exact deterministic snapshot a human sees (viewport lines, cursor position/visibility, size). Optionally includes per-row attribute encodings. Read-only. Get paneId from novaterminal.list_sessions.")]
    public static async Task<string> ReadScreen(
        AgentHostClient client,
        [Description("The pane id (GUID) from novaterminal.list_sessions.")] string paneId,
        [Description("Include per-row attribute lines (flags:fg/bg per cell). Default false.")] bool includeAttributes = false,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(paneId, out var pane))
        {
            return $"Error: '{paneId}' is not a valid pane id (GUID). Use novaterminal.list_sessions to find live pane ids.";
        }

        var parameters = JsonSerializer.SerializeToElement(
            new ReadScreenParams { PaneId = pane, IncludeAttributes = includeAttributes },
            AgentHostJsonContext.Default.ReadScreenParams);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ReadScreen, parameters, cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        return TryDeserializeResult(result, AgentHostJsonContext.Default.ScreenSnapshotDto, out var dto)
            ? FormatScreen(dto!)
            : AgentHostClient.ProtocolErrorMessage;
    }

    [McpServerTool(Name = "novaterminal.read_scrollback"),
     Description("Reads a range of scrollback (history) lines from a live NovaTerminal session, oldest first. Ranged and capped per request; the result reports the effective start line and the total lines available so you can page. Read-only. Get paneId from novaterminal.list_sessions.")]
    public static async Task<string> ReadScrollback(
        AgentHostClient client,
        [Description("The pane id (GUID) from novaterminal.list_sessions.")] string paneId,
        [Description("First scrollback line to return (0 = oldest retained line). Default 0.")] int startLine = 0,
        [Description("Maximum lines to return (server-capped). Default 200.")] int maxLines = 200,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(paneId, out var pane))
        {
            return $"Error: '{paneId}' is not a valid pane id (GUID). Use novaterminal.list_sessions to find live pane ids.";
        }
        if (maxLines <= 0)
        {
            // Guarded here, before any IPC: the server clamps this to an empty
            // page whose paging hint would point at the same startLine — an
            // agent following the documented paging flow would loop forever.
            return "Error: maxLines must be greater than 0.";
        }
        if (startLine < 0)
        {
            return "Error: startLine must be 0 or greater (0 = oldest retained line).";
        }

        var parameters = JsonSerializer.SerializeToElement(
            new ReadScrollbackParams { PaneId = pane, StartLine = startLine, MaxLines = maxLines },
            AgentHostJsonContext.Default.ReadScrollbackParams);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ReadScrollback, parameters, cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        return TryDeserializeResult(result, AgentHostJsonContext.Default.ReadScrollbackResult, out var dto)
            ? FormatScrollback(dto!)
            : AgentHostClient.ProtocolErrorMessage;
    }

    [McpServerTool(Name = "novaterminal.get_session_status"),
     Description("Reports what a live NovaTerminal session is doing right now: running / awaitingInput / idle / exited, with a confidence tier (precise = shell-integration events; heuristic = PTY signals), the in-flight command when known, exit code, and stall state. Read-only. Get paneId from novaterminal.list_sessions.")]
    public static async Task<string> GetSessionStatus(
        AgentHostClient client,
        [Description("The pane id (GUID) from novaterminal.list_sessions.")] string paneId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(paneId, out var pane))
        {
            return $"Error: '{paneId}' is not a valid pane id (GUID). Use novaterminal.list_sessions to find live pane ids.";
        }

        var parameters = JsonSerializer.SerializeToElement(
            new GetSessionStatusParams { PaneId = pane },
            AgentHostJsonContext.Default.GetSessionStatusParams);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.GetSessionStatus, parameters, cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        return TryDeserializeResult(result, AgentHostJsonContext.Default.SessionStatusDto, out var dto)
            ? FormatStatus(dto!)
            : AgentHostClient.ProtocolErrorMessage;
    }

    [McpServerTool(Name = "novaterminal.wait_for_events"),
     Description("Waits for session events from the running NovaTerminal app: status changes, command completions (with exit code and duration), bells, stalls, and sessions opening/closing. Long-poll with a cursor: pass sinceSeq=0 on the first call, then the nextSeq from each result. Returns immediately when events are pending, otherwise parks up to timeoutMs (server-capped at 25000). An empty result means the timeout elapsed — call again with the same cursor. Read-only.")]
    public static async Task<string> WaitForEvents(
        AgentHostClient client,
        [Description("Cursor: deliver events newer than this sequence number. 0 on the first call, then the previous result's nextSeq.")] long sinceSeq = 0,
        [Description("How long to wait when no events are pending, in milliseconds (0–25000). Default 20000.")] int timeoutMs = 20_000,
        CancellationToken cancellationToken = default)
    {
        if (sinceSeq < 0)
        {
            return "Error: sinceSeq must be 0 or greater (0 = from the oldest retained event).";
        }

        var clampedTimeout = Math.Clamp(timeoutMs, 0, AgentHostProtocol.MaxWaitForEventsTimeoutMs);
        var parameters = JsonSerializer.SerializeToElement(
            new WaitForEventsParams { SinceSeq = sinceSeq, TimeoutMs = clampedTimeout },
            AgentHostJsonContext.Default.WaitForEventsParams);
        var outcome = await client.CallAsync(
            AgentHostProtocol.Methods.WaitForEvents,
            parameters,
            cancellationToken,
            roundTripTimeout: TimeSpan.FromMilliseconds(clampedTimeout + 5_000)).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        return TryDeserializeResult(result, AgentHostJsonContext.Default.WaitForEventsResult, out var dto)
            ? FormatEvents(dto!, sinceSeq)
            : AgentHostClient.ProtocolErrorMessage;
    }

    [McpServerTool(Name = "novaterminal.export_replay"),
     Description("Exports a live NovaTerminal session's recent terminal output as a deterministic replay (.rec) file and returns its path — use it for postmortems of what happened in a session ('debug what your agent did, frame by frame'). The file contains output and window resizes only, NEVER anything the user typed. Requires BOTH 'Agent access (observe)' and its 'Agent replay export' sub-toggle in NovaTerminal settings. The recording window is bounded, so long sessions export only the most recent activity (the result says when it was truncated). Replay the file headlessly with: NovaTerminal.Cli --replay <path>. Get paneId from novaterminal.list_sessions.")]
    public static async Task<string> ExportReplay(
        AgentHostClient client,
        [Description("The pane id (GUID) from novaterminal.list_sessions.")] string paneId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(paneId, out var pane))
        {
            return $"Error: '{paneId}' is not a valid pane id (GUID). Use novaterminal.list_sessions to find live pane ids.";
        }

        var parameters = JsonSerializer.SerializeToElement(
            new ExportReplayParams { PaneId = pane },
            AgentHostJsonContext.Default.ExportReplayParams);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ExportReplay, parameters, cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        return TryDeserializeResult(result, AgentHostJsonContext.Default.ExportReplayResult, out var dto)
            ? FormatExport(dto!)
            : AgentHostClient.ProtocolErrorMessage;
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Deserializes a result payload, treating malformed/empty payloads as a
    /// protocol error rather than throwing out of the tool. Contract note: the
    /// DTOs' array/string members are declared <c>required</c> and non-nullable,
    /// so a successful deserialization here cannot yield null members — a
    /// missing required field lands in the JsonException path instead.
    /// </summary>
    private static bool TryDeserializeResult<T>(
        JsonElement result,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        out T? value)
        where T : class
    {
        try
        {
            value = result.Deserialize(typeInfo);
            return value != null;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryUnwrap(AgentHostClient.CallOutcome outcome, out JsonElement result, out string error)
    {
        result = default;
        if (!outcome.Available)
        {
            error = outcome.UnavailableReason ?? AgentHostClient.UnavailableMessage;
            return false;
        }

        var response = outcome.Response!;
        if (response.Error != null)
        {
            error = $"Error ({response.Error.Code}): {response.Error.Message}";
            return false;
        }

        if (response.Result == null)
        {
            error = AgentHostClient.UnavailableMessage;
            return false;
        }

        result = response.Result.Value;
        error = string.Empty;
        return true;
    }

    // ── Formatting (internal for tests) ─────────────────────────────────────

    internal static string FormatSessionList(SessionInfo[] sessions)
    {
        if (sessions.Length == 0)
        {
            return "No live sessions. NovaTerminal is running with Agent Access enabled, but no terminal panes are open.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{sessions.Length} live session(s):");
        sb.AppendLine();
        sb.AppendLine("| paneId | title | profile | kind | size | active | status | tabId |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in sessions)
        {
            var status = s.Status == null ? "-" : $"{s.Status} ({s.Confidence})";
            sb.AppendLine(
                $"| {s.PaneId} | {s.Title} | {s.ProfileName} | {s.Kind} | {s.Cols}x{s.Rows} | {(s.IsActive ? "yes" : "no")} | {status} | {(s.TabId?.ToString() ?? "-")} |");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string FormatStatus(SessionStatusDto dto)
    {
        var sb = new StringBuilder();
        sb.Append($"Session {dto.PaneId}: {dto.Status} ({dto.Confidence} confidence)");
        if (dto.CurrentCommand != null) sb.Append($", command: {dto.CurrentCommand}");
        if (dto.ExitCode is { } exit) sb.Append($", exit code {exit}");
        if (dto.IsStalled) sb.Append($" — STALLED (no output for at least {dto.StallThresholdSeconds}s)");
        sb.AppendLine();
        sb.AppendLine($"Status since: {FormatUtc(dto.StatusSinceMs)}; last output: {FormatUtc(dto.LastOutputAtMs)}.");
        sb.Append($"Thresholds: stall after {dto.StallThresholdSeconds}s of silence while running, idle after {dto.IdleThresholdSeconds}s at a prompt.");
        return sb.ToString();
    }

    internal static string FormatEvents(WaitForEventsResult dto, long sinceSeq)
    {
        var sb = new StringBuilder();
        if (dto.Events.Length == 0)
        {
            sb.Append($"No events within the wait window. Call again with sinceSeq={dto.NextSeq}.");
            return sb.ToString();
        }

        sb.AppendLine($"{dto.Events.Length} event(s). Next call: sinceSeq={dto.NextSeq}.");
        if (sinceSeq > 0 && sinceSeq + 1 < dto.OldestSeq)
        {
            sb.AppendLine($"Warning: events {sinceSeq + 1}–{dto.OldestSeq - 1} were evicted before this read — some events were missed. Resynchronize with novaterminal.list_sessions / get_session_status.");
        }
        sb.AppendLine();
        sb.AppendLine("| seq | time (UTC) | paneId | type | status | details |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in dto.Events)
        {
            var details = e.Type == AgentHostProtocol.EventTypes.CommandFinished
                ? $"exit {(e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")}{(e.DurationMs is { } d ? $", {d} ms" : "")}"
                : e.ExitCode is { } code ? $"exit {code}" : "-";
            sb.AppendLine($"| {e.Seq} | {FormatUtc(e.TimestampMs)} | {e.PaneId} | {e.Type} | {e.Status} | {details} |");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string FormatExport(ExportReplayResult dto)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Replay exported: {dto.FilePath}");
        if (dto.EventCount == 0)
        {
            sb.AppendLine("The recording window contained no events yet (the session produced no output since recording started); the file has a valid header and nothing else.");
        }
        else
        {
            var durationMs = Math.Max(0, dto.LastEventMs - dto.FirstEventMs);
            sb.AppendLine($"{dto.EventCount} event(s) covering {durationMs} ms of activity (output and resizes only — input is never recorded).");
        }
        if (dto.TruncatedAtStart)
        {
            sb.AppendLine("Note: older activity had already been evicted from the bounded recording window — this export is a suffix of the session, not its full history.");
        }
        sb.Append("Replay it headlessly with: NovaTerminal.Cli --replay <path> (renders the final screen deterministically).");
        return sb.ToString();
    }

    private static string FormatUtc(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    internal static string FormatScreen(ScreenSnapshotDto dto)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Screen {dto.Cols}x{dto.Rows}, cursor at row {dto.CursorRow}, col {dto.CursorCol} ({(dto.CursorVisible ? "visible" : "hidden")}). Lines are numbered from 0; trailing whitespace is trimmed.");
        sb.AppendLine();
        for (var i = 0; i < dto.Lines.Length; i++)
        {
            sb.AppendLine($"{i,3}| {dto.Lines[i]}");
        }

        if (dto.AttributeLines is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Attributes (per row: flagsHex:fgIndex/bgIndex per cell, '|' separated):");
            for (var i = 0; i < dto.AttributeLines.Length; i++)
            {
                sb.AppendLine($"{i,3}| {dto.AttributeLines[i]}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    internal static string FormatScrollback(ReadScrollbackResult dto)
    {
        var sb = new StringBuilder();
        var end = dto.StartLine + dto.Lines.Length;
        sb.AppendLine($"Scrollback lines {dto.StartLine}–{Math.Max(dto.StartLine, end - 1)} of {dto.TotalLines} total (oldest first).");
        if (end < dto.TotalLines)
        {
            sb.AppendLine($"More available: call again with startLine={end}.");
        }
        sb.AppendLine();
        for (var i = 0; i < dto.Lines.Length; i++)
        {
            sb.AppendLine($"{dto.StartLine + i,5}| {dto.Lines[i]}");
        }
        return sb.ToString().TrimEnd();
    }
}
