using System;

namespace NovaTerminal.AgentHost
{
    /// <summary>What a session is doing right now (agent-host A2 status model).</summary>
    public enum AgentSessionStatusKind
    {
        /// <summary>A command or full-screen TUI is executing.</summary>
        Running,

        /// <summary>The shell is at a prompt waiting for input.</summary>
        AwaitingInput,

        /// <summary>At a prompt with no activity for <see cref="AgentSessionStatusMachine.IdleThresholdSeconds"/>.</summary>
        Idle,

        /// <summary>The session's process has exited.</summary>
        Exited,
    }

    /// <summary>
    /// How the status was derived. Precise means shell-integration events
    /// (prompt/command lifecycle) are driving the machine; heuristic means
    /// PTY-level signals only (child processes, alt screen, output activity).
    /// </summary>
    public enum AgentSessionStatusConfidence
    {
        Heuristic,
        Precise,
    }

    public enum AgentSessionEventType
    {
        StatusChanged,
        CommandFinished,
        Bell,
        Stalled,
    }

    /// <summary>Point-in-time status, safe to read from any thread via <see cref="AgentSessionStatusMachine.Snapshot"/>.</summary>
    public sealed record AgentSessionStatusSnapshot
    {
        public required AgentSessionStatusKind Kind { get; init; }
        public required AgentSessionStatusConfidence Confidence { get; init; }
        public int? ExitCode { get; init; }
        public string? CurrentCommand { get; init; }
        public required DateTimeOffset StatusSince { get; init; }
        public required DateTimeOffset LastOutputAt { get; init; }
        public required bool IsStalled { get; init; }
        public int StallThresholdSeconds { get; init; } = AgentSessionStatusMachine.StallThresholdSeconds;
        public int IdleThresholdSeconds { get; init; } = AgentSessionStatusMachine.IdleThresholdSeconds;
    }

    /// <summary>
    /// A transition emitted by the status machine. PR2 of the A2 design feeds
    /// these into the event ring behind <c>waitForEvents</c>; until then they
    /// exist for tests and future consumers.
    /// </summary>
    public sealed record AgentSessionStatusEvent
    {
        public required AgentSessionEventType Type { get; init; }
        public required AgentSessionStatusKind Status { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public int? ExitCode { get; init; }
        public TimeSpan? Duration { get; init; }
    }
}
