using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>
/// A single request frame on the control channel. <see cref="Params"/> is a
/// raw JSON element so the frame envelope stays method-agnostic and
/// source-generator friendly (no polymorphic serialization); the server
/// deserializes it into the per-method params type after dispatching on
/// <see cref="Method"/>.
/// </summary>
public sealed record AgentHostRequest
{
    [JsonPropertyName("v")]
    public required int Version { get; init; }

    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>
/// A single response frame. Exactly one of <see cref="Result"/> or
/// <see cref="Error"/> is set. "App not running" is not representable here by
/// design — that condition surfaces client-side as a failed connection.
/// </summary>
public sealed record AgentHostResponse
{
    [JsonPropertyName("v")]
    public required int Version { get; init; }

    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public AgentHostError? Error { get; init; }
}

/// <summary>Error payload; <see cref="Code"/> is one of <see cref="AgentHostProtocol.ErrorCodes"/>.</summary>
public sealed record AgentHostError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Content of the <see cref="AgentHostProtocol.DiscoveryFileName"/> file the
/// app writes next to settings.json while the endpoint is listening. A stale
/// file (dead <see cref="Pid"/>) is replaced by the next app instance.
/// </summary>
public sealed record EndpointDescriptor
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    /// <summary>Pipe name (Windows) or absolute socket path (Linux/macOS).</summary>
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }

    [JsonPropertyName("pid")]
    public required int Pid { get; init; }
}
