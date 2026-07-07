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

        // The PTY session behind this registration, published by the pane on the
        // UI thread whenever the session is created, swapped, or torn down —
        // the same push pattern as the metadata snapshot, so the endpoint's
        // sweep never dereferences pane state. Volatile: a reference published
        // here is safely visible to the timer thread. Widened from
        // ITerminalLifecycle in A4 so the endpoint can also reach the
        // flight-recorder surface (ITerminalFlightRecorder) without touching
        // the pane; the sweep still uses only the lifecycle members.
        private volatile NovaTerminal.Pty.ITerminalSession? _session;

        // Desired flight-recording state pushed by the endpoint (A4). Kept on
        // the registration because the pane may publish the session *after*
        // the endpoint enabled recording (registration happens before spawn),
        // and reconnects swap sessions: every newly published session must
        // inherit the endpoint's decision. 0 = disabled.
        private long _flightRecordingMaxBytes;

        /// <summary>Publishes (or clears) the PTY session this registration runs on. UI thread.</summary>
        public void SetLifecycle(NovaTerminal.Pty.ITerminalSession? session)
        {
            var previous = _session;
            _session = session;

            // The flight ring follows the observe lifecycle of *this*
            // registration: a session this registration no longer owns
            // (reconnect swap, detach) must not keep retaining output until
            // its eventual disposal.
            if (previous != null && !ReferenceEquals(previous, session))
            {
                try
                {
                    previous.DisableFlightRecording();
                }
                catch
                {
                    // raced a dispose — the ring died with the session
                }
            }

            if (session != null)
            {
                var maxBytes = System.Threading.Interlocked.Read(ref _flightRecordingMaxBytes);
                if (maxBytes > 0)
                {
                    TryApplyFlightRecording(session, maxBytes);
                }
            }
        }

        /// <summary>
        /// Endpoint lifecycle (A4): start retaining recent output on the
        /// current session and every session published later, bounded by
        /// <paramref name="maxBytes"/>. Idempotent.
        /// </summary>
        public void EnableFlightRecording(long maxBytes)
        {
            System.Threading.Interlocked.Exchange(ref _flightRecordingMaxBytes, maxBytes);
            if (_session is { } session)
            {
                TryApplyFlightRecording(session, maxBytes);
            }
        }

        /// <summary>Endpoint lifecycle (A4): stop retaining and drop the ring. Idempotent.</summary>
        public void DisableFlightRecording()
        {
            System.Threading.Interlocked.Exchange(ref _flightRecordingMaxBytes, 0);
            var session = _session;
            if (session == null) return;
            try
            {
                session.DisableFlightRecording();
            }
            catch
            {
                // raced a dispose — the ring died with the session
            }
        }

        /// <summary>
        /// Exports the session's flight recording to <paramref name="filePath"/>.
        /// False when no session is published, recording is not enabled, or the
        /// write failed (the session logs the reason).
        /// </summary>
        public bool TryExportFlightRecording(string filePath, out NovaTerminal.Replay.FlightExportInfo info)
        {
            var session = _session;
            if (session == null)
            {
                info = default;
                return false;
            }

            try
            {
                return session.TryExportFlightRecording(filePath, out info);
            }
            catch
            {
                info = default;
                return false; // raced a dispose — treat as unavailable
            }
        }

        private static void TryApplyFlightRecording(NovaTerminal.Pty.ITerminalSession session, long maxBytes)
        {
            try
            {
                session.EnableFlightRecording(maxBytes);
            }
            catch
            {
                // raced a dispose — the next published session will inherit the state
            }
        }

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
            var lifecycle = _session;
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
