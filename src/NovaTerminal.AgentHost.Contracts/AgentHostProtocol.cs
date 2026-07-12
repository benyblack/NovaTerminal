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

    /// <summary>Server-side cap on a waitForEvents long-poll (client read timeouts must exceed this).</summary>
    public const int MaxWaitForEventsTimeoutMs = 25_000;

    /// <summary>Capacity of the server's event ring; older events are evicted and reported via oldestSeq.</summary>
    public const int EventRingCapacity = 256;

    /// <summary>
    /// Per-session retention budget for the flight recorder ring behind
    /// <c>exportReplay</c> (A4): total payload bytes plus a fixed per-event
    /// overhead of recent raw output/resize events kept in memory while the
    /// observe endpoint is running.
    /// </summary>
    public const long FlightRecorderMaxBytesPerSession = 2 * 1024 * 1024;

    /// <summary>Subfolder of the recordings directory where agent-triggered exports land (A4).</summary>
    public const string AgentExportsSubdirectory = "agent-exports";

    /// <summary>Method names. Observe-only; later milestones append, they never repurpose.</summary>
    public static class Methods
    {
        public const string ListSessions = "listSessions";
        public const string ReadScreen = "readScreen";
        public const string ReadScrollback = "readScrollback";
        public const string GetSessionStatus = "getSessionStatus";
        public const string WaitForEvents = "waitForEvents";

        /// <summary>
        /// A4: writes a session's flight recording (recent output + resizes,
        /// never input) to a replay v2 file and returns its path. Additive in
        /// protocol version 1; requires the replay-export setting on top of
        /// the observe toggle.
        /// </summary>
        public const string ExportReplay = "exportReplay";
    }

    /// <summary>Wire values for session status (A2). See the A2 design doc for exact semantics.</summary>
    public static class StatusKinds
    {
        public const string Running = "running";
        public const string AwaitingInput = "awaitingInput";
        public const string Idle = "idle";
        public const string Exited = "exited";
    }

    /// <summary>Wire values for status confidence: how the status was derived.</summary>
    public static class StatusConfidences
    {
        /// <summary>Shell-integration events (prompt/command lifecycle) drive the status.</summary>
        public const string Precise = "precise";

        /// <summary>PTY-level signals only (child processes, alt screen, output activity).</summary>
        public const string Heuristic = "heuristic";
    }

    /// <summary>Wire values for event types on the waitForEvents channel.</summary>
    public static class EventTypes
    {
        public const string StatusChanged = "statusChanged";
        public const string CommandFinished = "commandFinished";
        public const string Bell = "bell";
        public const string Stalled = "stalled";
        public const string SessionOpened = "sessionOpened";
        public const string SessionClosed = "sessionClosed";
    }

    /// <summary>Stable machine-readable error codes carried in <see cref="AgentHostError.Code"/>.</summary>
    public static class ErrorCodes
    {
        public const string VersionMismatch = "versionMismatch";
        public const string MalformedRequest = "malformedRequest";
        public const string UnknownMethod = "unknownMethod";
        public const string SessionNotFound = "sessionNotFound";
        public const string Internal = "internal";

        /// <summary>
        /// A4: <c>exportReplay</c> was called but the user has not enabled
        /// "Agent replay export" in settings (a second default-off gate on top
        /// of the observe toggle, per the DIRECTION permission table).
        /// </summary>
        public const string ExportDisabled = "exportDisabled";

        /// <summary>
        /// A4: the session exists but no flight recording is available to
        /// export right now (its PTY session is not yet published, was torn
        /// down, or the export failed to write).
        /// </summary>
        public const string ExportUnavailable = "exportUnavailable";
    }
}
