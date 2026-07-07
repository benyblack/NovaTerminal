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
