namespace NovaTerminal.AgentHost.Contracts;

/// <summary>
/// Protocol constants for the agent-host control channel between the running
/// app (endpoint) and the MCP server (client). See
/// docs/plans/2026-07-07-agent-host-a1-observe-design.md.
///
/// Wire format: newline-delimited JSON frames (<see cref="AgentHostRequest"/> /
/// <see cref="AgentHostResponse"/>), UTF-8, one frame per line. The transport
/// is a per-user local endpoint: a named pipe on Windows
/// (<see cref="WindowsPipeNamePrefix"/> + user SID, CurrentUserOnly), a unix
/// domain socket (mode 0600) on Linux/macOS. The endpoint is discovered via
/// <see cref="DiscoveryFileName"/> next to settings.json.
/// </summary>
public static class AgentHostProtocol
{
    /// <summary>
    /// Wire protocol version. The server rejects requests whose version does
    /// not match with <see cref="ErrorCodes.VersionMismatch"/>.
    /// </summary>
    public const int Version = 1;

    /// <summary>Discovery file written next to settings.json while the endpoint is up.</summary>
    public const string DiscoveryFileName = "agent-endpoint.json";

    /// <summary>Windows named-pipe name prefix; the user SID is appended.</summary>
    public const string WindowsPipeNamePrefix = "novaterminal-agent-";

    /// <summary>Unix domain socket file name (created in the app's runtime directory).</summary>
    public const string UnixSocketFileName = "agent.sock";

    /// <summary>Server-side cap on scrollback lines returned per request.</summary>
    public const int MaxScrollbackLinesPerRequest = 2000;

    /// <summary>Method names for A1 (observe-only). Later milestones append; they never repurpose.</summary>
    public static class Methods
    {
        public const string ListSessions = "listSessions";
        public const string ReadScreen = "readScreen";
        public const string ReadScrollback = "readScrollback";
    }

    /// <summary>Stable machine-readable error codes carried in <see cref="AgentHostError.Code"/>.</summary>
    public static class ErrorCodes
    {
        public const string VersionMismatch = "versionMismatch";
        public const string MalformedRequest = "malformedRequest";
        public const string UnknownMethod = "unknownMethod";
        public const string SessionNotFound = "sessionNotFound";
        public const string Internal = "internal";
    }
}
