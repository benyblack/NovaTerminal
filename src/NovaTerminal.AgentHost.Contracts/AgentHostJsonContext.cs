using System.Text.Json.Serialization;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>
/// Source-generated JSON context for every wire type on the control channel.
/// Both ends (app endpoint, MCP-server client) must serialize exclusively
/// through this context: it is reflection-free (Native AOT safe, matching the
/// release bundles) and keeps the wire shape identical on both sides.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentHostRequest))]
[JsonSerializable(typeof(AgentHostResponse))]
[JsonSerializable(typeof(AgentHostError))]
[JsonSerializable(typeof(EndpointDescriptor))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(ListSessionsResult))]
[JsonSerializable(typeof(ReadScreenParams))]
[JsonSerializable(typeof(ScreenSnapshotDto))]
[JsonSerializable(typeof(ReadScrollbackParams))]
[JsonSerializable(typeof(ReadScrollbackResult))]
[JsonSerializable(typeof(GetSessionStatusParams))]
[JsonSerializable(typeof(SessionStatusDto))]
[JsonSerializable(typeof(AgentEventDto))]
[JsonSerializable(typeof(WaitForEventsParams))]
[JsonSerializable(typeof(WaitForEventsResult))]
[JsonSerializable(typeof(ExportReplayParams))]
[JsonSerializable(typeof(ExportReplayResult))]
[JsonSerializable(typeof(SendInputParams))]
[JsonSerializable(typeof(SendInputResult))]
[JsonSerializable(typeof(SpawnSessionParams))]
[JsonSerializable(typeof(SpawnSessionResult))]
[JsonSerializable(typeof(CloseSessionParams))]
[JsonSerializable(typeof(CloseSessionResult))]
public sealed partial class AgentHostJsonContext : JsonSerializerContext
{
}
