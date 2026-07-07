using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>Params for <c>getSessionStatus</c>.</summary>
public sealed record GetSessionStatusParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }
}

/// <summary>
/// Result payload for <c>getSessionStatus</c>. Timestamps are Unix epoch
/// milliseconds; thresholds are echoed so clients never hard-code them.
/// </summary>
public sealed record SessionStatusDto
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>One of <see cref="AgentHostProtocol.StatusKinds"/>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>One of <see cref="AgentHostProtocol.StatusConfidences"/>.</summary>
    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>The command in flight / last accepted, when shell integration reports it.</summary>
    [JsonPropertyName("currentCommand")]
    public string? CurrentCommand { get; init; }

    [JsonPropertyName("statusSinceMs")]
    public required long StatusSinceMs { get; init; }

    [JsonPropertyName("lastOutputAtMs")]
    public required long LastOutputAtMs { get; init; }

    /// <summary>Running with no output for at least <see cref="StallThresholdSeconds"/>.</summary>
    [JsonPropertyName("isStalled")]
    public required bool IsStalled { get; init; }

    [JsonPropertyName("stallThresholdSeconds")]
    public required int StallThresholdSeconds { get; init; }

    [JsonPropertyName("idleThresholdSeconds")]
    public required int IdleThresholdSeconds { get; init; }
}

/// <summary>One event on the <c>waitForEvents</c> channel.</summary>
public sealed record AgentEventDto
{
    /// <summary>Monotonically increasing across the whole endpoint; the long-poll cursor.</summary>
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("timestampMs")]
    public required long TimestampMs { get; init; }

    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>One of <see cref="AgentHostProtocol.EventTypes"/>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Session status at emission time (<see cref="AgentHostProtocol.StatusKinds"/>).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>Command duration for <c>commandFinished</c> events.</summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; init; }
}

/// <summary>Params for <c>waitForEvents</c>.</summary>
public sealed record WaitForEventsParams
{
    /// <summary>Deliver events with seq greater than this. Pass 0 on the first call, then the previous nextSeq.</summary>
    [JsonPropertyName("sinceSeq")]
    public required long SinceSeq { get; init; }

    /// <summary>How long to park when no events are pending; server-capped at <see cref="AgentHostProtocol.MaxWaitForEventsTimeoutMs"/>.</summary>
    [JsonPropertyName("timeoutMs")]
    public required int TimeoutMs { get; init; }
}

/// <summary>
/// Result payload for <c>waitForEvents</c>. Empty <see cref="Events"/> means
/// the timeout elapsed. If <c>sinceSeq + 1 &lt; oldestSeq</c>, events were
/// evicted before the caller read them — the caller knows exactly that it
/// missed some and can resynchronize via list/status calls.
/// </summary>
public sealed record WaitForEventsResult
{
    [JsonPropertyName("events")]
    public required AgentEventDto[] Events { get; init; }

    /// <summary>Cursor to pass as the next sinceSeq.</summary>
    [JsonPropertyName("nextSeq")]
    public required long NextSeq { get; init; }

    /// <summary>Seq of the oldest event still retained (0 when the ring is empty).</summary>
    [JsonPropertyName("oldestSeq")]
    public required long OldestSeq { get; init; }
}
