using System;
using NovaTerminal.VT;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// One live pane's entry in <see cref="AgentSessionRegistry"/>.
    ///
    /// Holds a lock-protected snapshot of the pane's metadata instead of live
    /// delegates into the control: the registry is queried from a background
    /// IPC thread (milestone A1/PR3), and Avalonia controls must not be read
    /// off the UI thread. The pane pushes updates on the UI thread
    /// (TerminalPane.UpdateAgentSessionSnapshot) whenever title, working
    /// directory, profile, or active state changes; readers on any thread get
    /// a consistent, tear-free view. Guid/bool fields are also read under the
    /// gate because Guid reads are not atomic.
    /// </summary>
    public sealed class AgentSessionRegistration
    {
        private readonly object _gate = new();
        private Guid _paneId;
        private Guid? _tabId;
        private string _title;
        private string _profileName;
        private string _kind;
        private bool _isActive;

        public AgentSessionRegistration(
            Guid paneId,
            TerminalBuffer buffer,
            string title,
            string profileName,
            string kind,
            bool isActive,
            Func<DateTimeOffset>? nowProvider = null)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            _paneId = paneId;
            Buffer = buffer;
            _title = title;
            _profileName = profileName;
            _kind = kind;
            _isActive = isActive;
            StatusMachine = new AgentSessionStatusMachine(nowProvider);
        }

        /// <summary>
        /// Per-session status state machine (A2). Signals are pushed by the
        /// pane on the UI thread; snapshots are safe from any thread.
        /// </summary>
        public AgentSessionStatusMachine StatusMachine { get; }

        // The PTY lifecycle behind this session, published by the pane on the
        // UI thread whenever the session is created, swapped, or torn down —
        // the same push pattern as the metadata snapshot, so the endpoint's
        // sweep never dereferences pane state. Volatile: a reference published
        // here is safely visible to the timer thread.
        private volatile NovaTerminal.Pty.ITerminalLifecycle? _lifecycle;

        /// <summary>Publishes (or clears) the PTY lifecycle this session runs on. UI thread.</summary>
        public void SetLifecycle(NovaTerminal.Pty.ITerminalLifecycle? lifecycle) => _lifecycle = lifecycle;

        /// <summary>
        /// PTY child-process probe for the heuristic status tier, invoked by
        /// the endpoint's 1 s sweep. Targets only the PTY layer
        /// (<c>ITerminalLifecycle.HasActiveChildProcesses</c>, thread-safe by
        /// contract) via the published reference above — never the pane. Null
        /// means "unknown right now" (no session yet, or the probe raced a
        /// teardown); the status machine keeps its last known value instead of
        /// flapping.
        /// </summary>
        public bool? ProbeHasActiveChildProcesses()
        {
            var lifecycle = _lifecycle;
            if (lifecycle == null) return null;
            try
            {
                return lifecycle.HasActiveChildProcesses;
            }
            catch
            {
                return null; // probe raced a dispose — unknown, not false
            }
        }

        /// <summary>The pane's VT buffer. Reads must take <see cref="TerminalBuffer.Lock"/> (endpoint milestone A1/PR3).</summary>
        public TerminalBuffer Buffer { get; }

        /// <summary>Stable pane identity; re-keyed via <see cref="AgentSessionRegistry.Rekey"/> on session restore.</summary>
        public Guid PaneId
        {
            get { lock (_gate) { return _paneId; } }
            internal set { lock (_gate) { _paneId = value; } }
        }

        /// <summary>Owning tab; null until MainWindow associates the pane via <see cref="AgentSessionRegistry.SetTabAssociation"/>.</summary>
        public Guid? TabId
        {
            get { lock (_gate) { return _tabId; } }
            internal set { lock (_gate) { _tabId = value; } }
        }

        /// <summary>Current display title (OSC title, or profile + cwd fallback) at the last snapshot push.</summary>
        public string Title
        {
            get { lock (_gate) { return _title; } }
        }

        /// <summary>Current profile name ("Terminal" when the pane has no profile) at the last snapshot push.</summary>
        public string ProfileName
        {
            get { lock (_gate) { return _profileName; } }
        }

        /// <summary>"ssh" or "local".</summary>
        public string Kind
        {
            get { lock (_gate) { return _kind; } }
        }

        /// <summary>True when this pane was the active pane of its tab at the last snapshot push.</summary>
        public bool IsActive
        {
            get { lock (_gate) { return _isActive; } }
        }

        /// <summary>Atomically replaces the pane-owned metadata. Called on the UI thread by the pane.</summary>
        public void UpdateSnapshot(string title, string profileName, string kind, bool isActive)
        {
            lock (_gate)
            {
                _title = title;
                _profileName = profileName;
                _kind = kind;
                _isActive = isActive;
            }
        }
    }
}
