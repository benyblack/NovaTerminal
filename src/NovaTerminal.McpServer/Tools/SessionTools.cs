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

        var sessions = result.Deserialize(AgentHostJsonContext.Default.ListSessionsResult)?.Sessions;
        return FormatSessionList(sessions ?? Array.Empty<SessionInfo>());
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

        var dto = result.Deserialize(AgentHostJsonContext.Default.ScreenSnapshotDto);
        return dto == null ? AgentHostClient.UnavailableMessage : FormatScreen(dto);
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

        var parameters = JsonSerializer.SerializeToElement(
            new ReadScrollbackParams { PaneId = pane, StartLine = startLine, MaxLines = maxLines },
            AgentHostJsonContext.Default.ReadScrollbackParams);
        var outcome = await client.CallAsync(AgentHostProtocol.Methods.ReadScrollback, parameters, cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(outcome, out var result, out var error)) return error;

        var dto = result.Deserialize(AgentHostJsonContext.Default.ReadScrollbackResult);
        return dto == null ? AgentHostClient.UnavailableMessage : FormatScrollback(dto);
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

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
        sb.AppendLine("| paneId | title | profile | kind | size | active | tabId |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in sessions)
        {
            sb.AppendLine(
                $"| {s.PaneId} | {s.Title} | {s.ProfileName} | {s.Kind} | {s.Cols}x{s.Rows} | {(s.IsActive ? "yes" : "no")} | {(s.TabId?.ToString() ?? "-")} |");
        }
        return sb.ToString().TrimEnd();
    }

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
