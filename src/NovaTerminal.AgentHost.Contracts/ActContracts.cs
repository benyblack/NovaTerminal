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
}

/// <summary>Result payload for <c>sendInput</c>.</summary>
public sealed record SendInputResult
{
    /// <summary>Number of UTF-8 bytes queued to the session.</summary>
    [JsonPropertyName("bytesSent")]
    public required int BytesSent { get; init; }
}
