using System;
using System.Collections.Concurrent;
using System.Linq;
using NovaTerminal.AgentHost.Contracts;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// Thread-safe registry of live terminal sessions for the agent-host
    /// control channel (docs/agent-host/DIRECTION.md, milestone A1; design in
    /// docs/plans/2026-07-07-agent-host-a1-observe-design.md).
    ///
    /// PR2 scope: pure bookkeeping. Panes register in <c>SetupCommon</c> and
    /// unregister in <c>DetachFromUiThread</c>; MainWindow associates tabs
    /// where it maintains <c>_paneOwnerTab</c>. The registry has no behavior
    /// until the IPC endpoint (PR3) queries it, and it never keeps a pane
    /// alive: entries are removed on dispose, not finalization.
    /// </summary>
    public sealed class AgentSessionRegistry
    {
        /// <summary>Process-wide instance used by the app wiring. Tests construct their own.</summary>
        public static AgentSessionRegistry Instance { get; } = new();

        private readonly ConcurrentDictionary<Guid, AgentSessionRegistration> _sessions = new();

        public int Count => _sessions.Count;

        /// <summary>Adds a registration. Returns false (and keeps the existing entry) on a duplicate PaneId.</summary>
        public bool Register(AgentSessionRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            return _sessions.TryAdd(registration.PaneId, registration);
        }

        /// <summary>Removes a registration; false when the pane was never registered (or already removed).</summary>
        public bool Unregister(Guid paneId) => _sessions.TryRemove(paneId, out _);

        public bool TryGet(Guid paneId, out AgentSessionRegistration registration)
        {
            var found = _sessions.TryGetValue(paneId, out var value);
            registration = value!;
            return found;
        }

        /// <summary>
        /// Moves a registration to a new pane id. Session restore assigns a
        /// persisted PaneId after construction (SessionManager.RestorePaneTree),
        /// i.e. after the pane has already registered; the PaneId setter calls
        /// this so the registry key always matches the pane. No-op when the old
        /// id is unknown; false when the new id is already taken.
        /// </summary>
        public bool Rekey(Guid oldPaneId, Guid newPaneId)
        {
            if (oldPaneId == newPaneId) return true;
            if (!_sessions.TryRemove(oldPaneId, out var registration)) return false;

            registration.PaneId = newPaneId;
            if (_sessions.TryAdd(newPaneId, registration)) return true;

            // New id collided (should not happen — PaneIds are GUIDs). Restore the old entry.
            registration.PaneId = oldPaneId;
            _sessions.TryAdd(oldPaneId, registration);
            return false;
        }

        /// <summary>Associates a pane with its owning tab. No-op for unknown panes.</summary>
        public bool SetTabAssociation(Guid paneId, Guid tabId)
        {
            if (!_sessions.TryGetValue(paneId, out var registration)) return false;
            registration.TabId = tabId;
            return true;
        }

        /// <summary>
        /// Point-in-time snapshot as wire DTOs, ordered by PaneId for a
        /// deterministic listing. Rows/Cols are read from the live buffer;
        /// they are informational here — actual screen reads (PR3) go through
        /// the deterministic snapshot path under the buffer lock.
        /// </summary>
        public SessionInfo[] ListSessions()
        {
            return _sessions.Values
                .Select(r => new SessionInfo
                {
                    PaneId = r.PaneId,
                    TabId = r.TabId,
                    Title = r.TitleProvider(),
                    ProfileName = r.ProfileNameProvider(),
                    Kind = r.KindProvider(),
                    Rows = r.Buffer.Rows,
                    Cols = r.Buffer.Cols,
                    IsActive = r.IsActiveProvider(),
                })
                .OrderBy(s => s.PaneId)
                .ToArray();
        }
    }
}
