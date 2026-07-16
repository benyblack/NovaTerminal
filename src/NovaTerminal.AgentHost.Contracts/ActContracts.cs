using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>Params for <c>sendInput</c> (A3, act surface).</summary>
public sealed record SendInputParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>
    /// Text to inject, exactly as a human would type it. May contain control
    /// characters (e.g. "" = Ctrl-C, "\r" = Enter). UTF-8 encoded and
    /// queued through the session's normal input path. Capped server-side at
    /// <see cref="AgentHostProtocol.MaxSendInputBytes"/>.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    /// When true, a single carriage return (U+000D) is appended after
    /// <see cref="Text"/> — the byte a console treats as "Enter". This is the
    /// reliable way for an agent to submit a command: many callers cannot emit a
    /// raw 0x0D through their tool-argument encoding (a literal newline arrives as
    /// a line feed, which PSReadLine treats as a soft line-continuation, and the
    /// "\r" escape often arrives as two literal characters). <see cref="Text"/>
    /// itself is still sent byte-faithfully; this only controls the trailing CR.
    /// Defaults to false.
    /// </summary>
    [JsonPropertyName("submit")]
    public bool Submit { get; init; }
}

/// <summary>Result payload for <c>sendInput</c>.</summary>
public sealed record SendInputResult
{
    /// <summary>Number of UTF-8 bytes queued to the session.</summary>
    [JsonPropertyName("bytesSent")]
    public required int BytesSent { get; init; }
}

/// <summary>Params for <c>spawnSession</c> (A3).</summary>
public sealed record SpawnSessionParams
{
    /// <summary>
    /// Profile name to open (case-insensitive; local settings profiles resolve
    /// before SSH store profiles on a collision). Null or empty opens the
    /// default local profile.
    /// </summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }
}

/// <summary>Result payload for <c>spawnSession</c>: identity of the newly opened pane.</summary>
public sealed record SpawnSessionResult
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    [JsonPropertyName("tabId")]
    public Guid? TabId { get; init; }

    [JsonPropertyName("profileName")]
    public required string ProfileName { get; init; }

    /// <summary>"local" or "ssh".</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

/// <summary>Params for <c>closeSession</c> (A3).</summary>
public sealed record CloseSessionParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }
}

/// <summary>Result payload for <c>closeSession</c>.</summary>
public sealed record CloseSessionResult
{
    [JsonPropertyName("closed")]
    public required bool Closed { get; init; }
}
