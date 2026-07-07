using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>One live terminal session (pane) as reported by <c>listSessions</c>.</summary>
public sealed record SessionInfo
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    [JsonPropertyName("tabId")]
    public required Guid TabId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("profileName")]
    public required string ProfileName { get; init; }

    /// <summary>"local" or "ssh".</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    /// <summary>True for the active pane of the active tab.</summary>
    [JsonPropertyName("isActive")]
    public required bool IsActive { get; init; }
}

/// <summary>Result payload for <c>listSessions</c>.</summary>
public sealed record ListSessionsResult
{
    [JsonPropertyName("sessions")]
    public required SessionInfo[] Sessions { get; init; }
}

/// <summary>Params for <c>readScreen</c>.</summary>
public sealed record ReadScreenParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>When true, per-row attribute lines are included (BufferSnapshot format).</summary>
    [JsonPropertyName("includeAttributes")]
    public bool IncludeAttributes { get; init; }
}

/// <summary>
/// Result payload for <c>readScreen</c>: a 1:1 projection of the deterministic
/// <c>NovaTerminal.Replay.BufferSnapshot</c> capture plus cursor state. The A1
/// parity test asserts this equals a direct <c>BufferSnapshot.Capture</c> of
/// the same buffer.
/// </summary>
public sealed record ScreenSnapshotDto
{
    /// <summary>Visible viewport, one string per row, top to bottom.</summary>
    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    /// <summary>Per-row attribute encoding; present only when requested.</summary>
    [JsonPropertyName("attributeLines")]
    public string[]? AttributeLines { get; init; }

    /// <summary>Cursor row in viewport coordinates (0-based).</summary>
    [JsonPropertyName("cursorRow")]
    public required int CursorRow { get; init; }

    /// <summary>Cursor column (0-based).</summary>
    [JsonPropertyName("cursorCol")]
    public required int CursorCol { get; init; }

    [JsonPropertyName("cursorVisible")]
    public required bool CursorVisible { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }
}

/// <summary>Params for <c>readScrollback</c> (ranged read, oldest line = 0).</summary>
public sealed record ReadScrollbackParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>First scrollback line to return (0-based, oldest first).</summary>
    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }

    /// <summary>
    /// Maximum lines to return; the server additionally caps this at
    /// <see cref="AgentHostProtocol.MaxScrollbackLinesPerRequest"/>.
    /// </summary>
    [JsonPropertyName("maxLines")]
    public required int MaxLines { get; init; }
}

/// <summary>Result payload for <c>readScrollback</c>.</summary>
public sealed record ReadScrollbackResult
{
    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    /// <summary>Echo of the effective start line after clamping.</summary>
    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }

    /// <summary>Total scrollback lines available at capture time.</summary>
    [JsonPropertyName("totalLines")]
    public required int TotalLines { get; init; }
}
