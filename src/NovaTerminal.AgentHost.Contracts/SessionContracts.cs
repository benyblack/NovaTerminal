using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>One live terminal session (pane) as reported by <c>listSessions</c>.</summary>
public sealed record SessionInfo
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>
    /// Owning tab, or null when the pane has not yet been associated with a
    /// tab (association happens lazily in the UI layer; freshly created split
    /// or replacement panes may briefly be unassociated). Clients must treat
    /// null as "unknown", never as an identity. Not <c>required</c>: the wire
    /// format omits the field entirely when null (WhenWritingNull).
    /// </summary>
    [JsonPropertyName("tabId")]
    public Guid? TabId { get; init; }

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

    /// <summary>
    /// Current status (<see cref="AgentHostProtocol.StatusKinds"/>), when the
    /// endpoint computes it (A2+). Null from older endpoints. Not required:
    /// the wire format omits it when null (WhenWritingNull).
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>How the status was derived (<see cref="AgentHostProtocol.StatusConfidences"/>); null when status is null.</summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }
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

/// <summary>Params for <c>captureScreen</c> (A5).</summary>
public sealed record CaptureScreenParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>
    /// When true the endpoint also returns the PNG bytes base64-encoded in
    /// <see cref="CaptureScreenResult.PngBase64"/>, so a caller that cannot read
    /// local files still sees the image. Dropped when the PNG exceeds
    /// <see cref="AgentHostProtocol.MaxInlineCaptureBytes"/>.
    /// </summary>
    [JsonPropertyName("inline")]
    public bool Inline { get; init; }

    /// <summary>
    /// Resample the capture down to at most this pixel width, preserving aspect
    /// ratio. 0 (the default) keeps the pane's native 1:1 size. The render itself
    /// always happens at 1:1; this only scales the result.
    /// </summary>
    [JsonPropertyName("maxWidth")]
    public int MaxWidth { get; init; }
}

/// <summary>Result payload for <c>captureScreen</c> (A5).</summary>
public sealed record CaptureScreenResult
{
    /// <summary>Absolute path of the written PNG.</summary>
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    /// <summary>Pixel width of the delivered image (after any downscale).</summary>
    [JsonPropertyName("width")]
    public required int Width { get; init; }

    /// <summary>Pixel height of the delivered image (after any downscale).</summary>
    [JsonPropertyName("height")]
    public required int Height { get; init; }

    /// <summary>Grid columns that were rendered.</summary>
    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    /// <summary>Grid rows that were rendered.</summary>
    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    /// <summary>Size of the PNG on disk, in bytes.</summary>
    [JsonPropertyName("byteCount")]
    public required int ByteCount { get; init; }

    /// <summary>True when <c>maxWidth</c> forced a resample.</summary>
    [JsonPropertyName("downscaled")]
    public required bool Downscaled { get; init; }

    /// <summary>
    /// Base64 PNG, present only when the caller asked for it and it fit under the
    /// inline cap. Omitted from the wire when null.
    /// </summary>
    [JsonPropertyName("pngBase64")]
    public string? PngBase64 { get; init; }

    /// <summary>
    /// True when the caller asked for an inline image and the endpoint dropped it
    /// for exceeding <see cref="AgentHostProtocol.MaxInlineCaptureBytes"/>. The
    /// file on disk is unaffected.
    /// </summary>
    [JsonPropertyName("inlineOmitted")]
    public bool InlineOmitted { get; init; }
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
