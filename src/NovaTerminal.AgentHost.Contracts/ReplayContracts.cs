using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>Params for <c>exportReplay</c> (A4).</summary>
public sealed record ExportReplayParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }
}

/// <summary>
/// Result payload for <c>exportReplay</c>: where the replay v2 file landed and
/// what window it covers. Privacy invariant: the file contains output and
/// resize events only — never input (typed keys are not retained by the flight
/// recorder at all; see docs/plans/2026-07-07-agent-host-a4-replay-design.md).
/// </summary>
public sealed record ExportReplayResult
{
    /// <summary>Absolute path of the written replay file.</summary>
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    /// <summary>Events written (output chunks + resizes).</summary>
    [JsonPropertyName("eventCount")]
    public required int EventCount { get; init; }

    /// <summary>
    /// First exported event, in milliseconds since flight recording started
    /// for this session. Timestamps inside the file are rebased so the first
    /// event is t=0; this locates the window on the session's timeline.
    /// </summary>
    [JsonPropertyName("firstEventMs")]
    public required long FirstEventMs { get; init; }

    /// <summary>Last exported event, same timeline as <see cref="FirstEventMs"/>.</summary>
    [JsonPropertyName("lastEventMs")]
    public required long LastEventMs { get; init; }

    /// <summary>True when older events had already been evicted from the bounded ring.</summary>
    [JsonPropertyName("truncatedAtStart")]
    public required bool TruncatedAtStart { get; init; }
}
