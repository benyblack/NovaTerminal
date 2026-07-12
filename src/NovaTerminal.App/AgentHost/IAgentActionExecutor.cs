using System;
using System.Threading.Tasks;

namespace NovaTerminal.AgentHost
{
    /// <summary>Outcome of an agent-requested spawn (A3).</summary>
    public readonly struct AgentSpawnResult
    {
        public AgentSpawnResult(Guid paneId, Guid? tabId, string profileName, string kind)
        {
            PaneId = paneId;
            TabId = tabId;
            ProfileName = profileName;
            Kind = kind;
        }

        public Guid PaneId { get; }
        public Guid? TabId { get; }
        public string ProfileName { get; }

        /// <summary>"local" or "ssh".</summary>
        public string Kind { get; }
    }

    /// <summary>
    /// Reason a spawn could not be satisfied, mapped to a protocol error code by
    /// the endpoint. Keeps Avalonia/UI concerns out of the service.
    /// </summary>
    public enum AgentSpawnError
    {
        /// <summary>No local or SSH profile matched the requested name.</summary>
        ProfileNotFound,

        /// <summary>An SSH profile matched but is not allowlisted for agent access.</summary>
        ProfileNotAllowed,

        /// <summary>The profile resolved but the tab failed to open.</summary>
        SpawnFailed,
    }

    /// <summary>
    /// UI-thread bridge the agent-host endpoint uses to open and close sessions
    /// (A3 spawn/close). Implemented by MainWindow and published on the service
    /// while the window lives; the service never touches Avalonia directly.
    /// Implementations marshal to the UI thread themselves.
    /// </summary>
    public interface IAgentActionExecutor
    {
        /// <summary>
        /// Opens a new tab for <paramref name="profileName"/> (null/empty = default
        /// local profile). Returns the spawned pane's identity, or an error reason.
        /// The allowlist check for SSH profiles happens inside the implementation
        /// (it owns profile resolution).
        /// </summary>
        Task<(AgentSpawnResult? Result, AgentSpawnError? Error)> SpawnAsync(string? profileName);

        /// <summary>Closes the pane with <paramref name="paneId"/>. False if no such live pane.</summary>
        Task<bool> ClosePaneAsync(Guid paneId);
    }
}
