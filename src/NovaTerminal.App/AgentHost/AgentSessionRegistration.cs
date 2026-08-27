using System;
using NovaTerminal.Shell;
using NovaTerminal.VT;

namespace NovaTerminal.AgentHost
{
    /// <summary>A rendered pane screenshot (A5), as returned by <see cref="AgentSessionRegistration.TryCapturePng"/>.</summary>
    public readonly record struct AgentCaptureInfo(
        byte[] Png,
        int Width,
        int Height,
        int Cols,
        int Rows,
        bool Downscaled);

    /// <summary>Why a capture could not be produced; mapped to a protocol error code by the endpoint.</summary>
    public enum AgentCaptureError
    {
        /// <summary>
        /// The pane has no usable render state right now: it has not been laid
        /// out and measured yet, is being torn down, or the render threw.
        /// </summary>
        Unavailable,

        /// <summary>The pane's grid would render larger than the per-capture pixel budget.</summary>
        TooLarge,
    }

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
        private Guid? _profileId;
        private bool _isAgentActable;

        // The window half of "the user can see this pane". Focus pushed to the
        // attention machine is the AND of this and _isActive: being the
        // selected pane only means "selected inside the app", which stays true
        // while the app is alt-tabbed away or minimized. Optimistic default so
        // a registration nobody tells about the window (every test that builds
        // one directly) behaves as it did before; MainWindow pushes the real
        // value the moment the registration reaches the registry, so the
        // default never survives into a running app.
        private bool _isWindowVisible = true;

        public AgentSessionRegistration(
            Guid paneId,
            TerminalBuffer buffer,
            string title,
            string profileName,
            string kind,
            bool isActive,
            Func<DateTimeOffset>? nowProvider = null,
            Guid? profileId = null)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            _paneId = paneId;
            Buffer = buffer;
            _title = title;
            _profileName = profileName;
            _kind = kind;
            _isActive = isActive;
            _profileId = profileId;
            StatusMachine = new AgentSessionStatusMachine(nowProvider);
            AttentionMachine = new AgentAttentionMachine(nowProvider);
            AttentionMachine.NoteFocusChanged(isActive);
        }

        /// <summary>
        /// Per-session status state machine (A2). Signals are pushed by the
        /// pane on the UI thread; snapshots are safe from any thread.
        /// </summary>
        public AgentSessionStatusMachine StatusMachine { get; }

        /// <summary>
        /// Per-pane agent attention tiers (read / wrote). Signals come from the
        /// endpoint on its IPC and timer threads and from the pane on the UI
        /// thread; the machine is internally locked.
        /// </summary>
        public AgentAttentionMachine AttentionMachine { get; }

        /// <summary>
        /// Whether an agent may currently act on this pane: the global act
        /// toggle plus, for SSH panes, the per-profile allowlist. Published by
        /// <see cref="AgentHostService"/> rather than derived here — the
        /// registration does not know the settings or the allowlist. Drives
        /// whether the pane shows its agent status segment at all.
        /// </summary>
        public bool IsAgentActable
        {
            get { lock (_gate) { return _isAgentActable; } }
            internal set
            {
                // AgentHostService.RefreshActability writes this unconditionally
                // from a 1 s sweep on every registration. Raising on every write
                // would turn that into a perpetual once-per-second UI-post storm
                // per pane, so the event fires only on an actual value change.
                bool changed;
                lock (_gate)
                {
                    changed = _isAgentActable != value;
                    _isAgentActable = value;
                }

                // Raised outside the gate, same rule UpdateSnapshot follows for
                // AttentionMachine.NoteFocusChanged: invoking a subscriber while
                // holding this registration's lock is a deadlock hazard (a
                // subscriber that calls back into this registration would
                // re-enter the lock on the same thread's call stack, or block
                // forever against another thread that already holds it).
                if (changed)
                {
                    ActabilityChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Raised outside <see cref="_gate"/> whenever <see cref="IsAgentActable"/>
        /// actually changes value (never on a same-value rewrite — see the
        /// setter). The owning pane uses this to re-render its status bar
        /// segment when act-reachability is republished with no attention-tier
        /// transition alongside it (global act toggle, allowlist edit).
        ///
        /// Deliberately payload-free: a handed-out <c>bool</c> is a stale
        /// channel. <c>AgentHostService.RefreshActability</c> can run
        /// concurrently (the 1 s sweep thread, the act-toggle setter, an
        /// allowlist edit on the UI thread) and two racing writers can compute
        /// different values for the same SSH pane. Whichever writer enters the
        /// setter first stores its value; it may then be descheduled and raise
        /// *after* the other. With a payload, the last value delivered can
        /// disagree with the value actually stored — and because the setter
        /// raises only on change, every later sweep sees no change and never
        /// corrects it, leaving a pane permanently unmarked while agents can
        /// type into it. With no payload the subscriber must read
        /// <see cref="IsAgentActable"/>, so whichever post runs last renders
        /// the current truth no matter how the raises interleave.
        /// </summary>
        public event Action? ActabilityChanged;

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

        /// <summary>
        /// Backing profile id (local settings profile or SSH store profile), or
        /// null for a profile-less pane. Used by the A3 act surface to check the
        /// per-profile SSH allowlist; null or a local session is governed by the
        /// global act toggle alone.
        /// </summary>
        public Guid? ProfileId
        {
            get { lock (_gate) { return _profileId; } }
        }

        /// <summary>
        /// Raised after <see cref="TrySendInput"/> has put bytes on the wire. The owning pane uses
        /// it to invalidate anything that models the command line from keystrokes alone: an agent
        /// typing for the user is text the keyboard path never saw.
        /// </summary>
        public Action? InputInjected { get; set; }

        /// <summary>
        /// Injects <paramref name="text"/> into the live session (A3). Returns
        /// false when no session is published or its process has already exited.
        /// Goes through <see cref="NovaTerminal.Pty.ITerminalIO.SendInput"/> —
        /// the same thread-safe, replay-recorded path human keystrokes take.
        /// </summary>
        public bool TrySendInput(string text)
        {
            var session = _session;
            if (session == null) return false;
            try
            {
                if (!session.IsProcessRunning) return false;
                session.SendInput(text);
                InputInjected?.Invoke();
                return true;
            }
            catch
            {
                return false; // raced a dispose
            }
        }

        /// <summary>True when this pane was the active pane of its tab at the last snapshot push.</summary>
        public bool IsActive
        {
            get { lock (_gate) { return _isActive; } }
        }

        // Render inputs for captureScreen (A5), pushed by the pane on the UI
        // thread whenever font metrics or font-related settings change. Kept as
        // plain values under the gate for the same reason as the metadata
        // snapshot: the endpoint renders on an IPC thread and must never read
        // the control. Null until the pane has measured its font.
        private PaneRenderParameters? _renderParameters;

        /// <summary>Publishes the pane's current render inputs. UI thread.</summary>
        public void UpdateRenderParameters(PaneRenderParameters parameters)
        {
            lock (_gate) { _renderParameters = parameters; }
        }

        /// <summary>The last published render inputs, or null when the pane has not been measured yet.</summary>
        public PaneRenderParameters? RenderParameters
        {
            get { lock (_gate) { return _renderParameters; } }
        }

        /// <summary>
        /// Renders the pane's visible grid to a PNG (A5), off the UI thread,
        /// through the same <see cref="TerminalSnapshotRenderer"/> the golden
        /// render tests use. <paramref name="maxWidth"/> (0 = no cap) resamples
        /// the result down afterwards; the render itself is always 1:1 so it
        /// cannot vary with the user's monitor scaling.
        /// </summary>
        public bool TryCapturePng(int maxWidth, out AgentCaptureInfo info, out AgentCaptureError error)
        {
            info = default;
            error = AgentCaptureError.Unavailable;

            if (RenderParameters is not { } parameters || !parameters.IsUsable)
            {
                return false;
            }

            var buffer = Buffer;
            int cols, rows;
            bool cursorVisible;
            buffer.Lock.EnterReadLock();
            try
            {
                cols = buffer.Cols;
                rows = buffer.Rows;
                cursorVisible = buffer.Modes.IsCursorVisible;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            if (cols <= 0 || rows <= 0)
            {
                return false;
            }

            // Cell metrics and grid size come from different places (the pane's
            // last publish vs. the buffer just now), so a capture that lands
            // between a font change and the resize it triggers pairs the new cell
            // size with the pre-resize row/column count. That is not a torn image:
            // the draw operation lays out exactly these rows/cols at exactly these
            // metrics, so the content fills the canvas either way — it is the same
            // one-frame transition the live control paints in that same window,
            // which is what a screenshot of a resizing pane should look like.
            var width = (int)Math.Ceiling(cols * (double)parameters.Metrics.CellWidth);
            var height = (int)Math.Ceiling(rows * (double)parameters.Metrics.CellHeight);
            if (width <= 0 || height <= 0)
            {
                return false;
            }
            if ((long)width * height > Contracts.AgentHostProtocol.MaxCapturePixels)
            {
                error = AgentCaptureError.TooLarge;
                return false;
            }

            var options = new TerminalSnapshotOptions
            {
                // What the user is looking at: their font, their theme, an opaque
                // background. Not what they have selected — a selection highlight
                // is transient UI state, and including it would make two captures
                // of the same output differ.
                FontResolution = SnapshotFontResolution.LiveParity,
                FillBackground = true,
                TypefaceFamily = parameters.FontFamily,
                FontSize = parameters.FontSize,
                EnableLigatures = parameters.EnableLigatures,
                EnableComplexShaping = parameters.EnableComplexShaping,

                // Follows the buffer's cursor mode, never the control's blink
                // phase or focus: a blinking cursor would make consecutive
                // captures of an idle session disagree.
                HideCursor = !cursorVisible,
            };

            try
            {
                using var bitmap = TerminalSnapshotRenderer.Capture(buffer, parameters.Metrics, width, height, options);
                using var downscaled = TerminalSnapshotRenderer.DownscaleToWidth(bitmap, maxWidth);
                var final = downscaled ?? bitmap;
                info = new AgentCaptureInfo(
                    TerminalSnapshotRenderer.EncodePng(final),
                    final.Width,
                    final.Height,
                    cols,
                    rows,
                    Downscaled: downscaled != null);
                return true;
            }
            catch (Exception ex)
            {
                // Font resolution, Skia allocation, or a torn-down buffer. The
                // capture is a read: failing it must never take the endpoint down.
                System.Diagnostics.Debug.WriteLine($"[AgentHost] captureScreen render failed: {ex}");
                return false;
            }
        }

        /// <summary>Atomically replaces the pane-owned metadata. Called on the UI thread by the pane.</summary>
        public void UpdateSnapshot(string title, string profileName, string kind, bool isActive, Guid? profileId = null)
        {
            bool effectiveFocus;
            lock (_gate)
            {
                _title = title;
                _profileName = profileName;
                _kind = kind;
                _isActive = isActive;
                _profileId = profileId;
                effectiveFocus = isActive && _isWindowVisible;
            }

            // Focus feeds the write-acknowledgement rule, and it means "the
            // user is plausibly looking at this pane" — being the selected pane
            // of a window that is minimized or behind another application does
            // not qualify. Pushed after the gate is released: the machine locks
            // internally and raises Changed.
            AttentionMachine.NoteFocusChanged(effectiveFocus);
        }

        /// <summary>
        /// The owning window became front-and-visible, or stopped being so
        /// (deactivated, minimized). Pushed by MainWindow from the UI thread.
        ///
        /// This exists because <see cref="UpdateSnapshot"/> is driven by
        /// pane-level changes only, and a window losing focus is not one: with
        /// focus meaning nothing but <c>IsActivePane</c>, an agent write into
        /// the selected pane of an alt-tabbed-away app would be retired by the
        /// periodic tick ten seconds later, with the user never having seen the
        /// one mark in this feature that is designed to survive until seen.
        ///
        /// Like <see cref="UpdateSnapshot"/>, the machine is signalled outside
        /// <see cref="_gate"/>: it locks internally and raises Changed to
        /// subscribers who may call back into this registration.
        /// </summary>
        public void NoteWindowVisibilityChanged(bool isWindowVisible)
        {
            bool effectiveFocus;
            lock (_gate)
            {
                if (_isWindowVisible == isWindowVisible) return;
                _isWindowVisible = isWindowVisible;
                effectiveFocus = _isActive && isWindowVisible;
            }

            AttentionMachine.NoteFocusChanged(effectiveFocus);
        }
    }
}
