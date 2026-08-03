using NovaTerminal.Shell;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using Avalonia.Controls.Presenters;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Threading;
using System.Net.NetworkInformation;
using System.Linq;
using Avalonia.Controls.Shapes;
using Avalonia.Automation;
using Avalonia.Platform.Storage;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using NovaTerminal.CommandAssist.ShellIntegration.Runtime;
using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.Platform.Ssh.Interactions;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Sessions;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;
using NovaTerminal.Pty;

namespace NovaTerminal.Controls
{
    public enum PaneAction
    {
        SplitVertical,
        SplitHorizontal,
        Equalize,
        ToggleZoom,
        ToggleBroadcast,
        Close
    }

    public enum RecordingNotificationKind
    {
        Started,
        Stopped,
        Failed
    }

    public sealed class RecordingNotificationEventArgs : EventArgs
    {
        public required RecordingNotificationKind Kind { get; init; }
        public required bool IsRecording { get; init; }
        public required string RecordingsDirectory { get; init; }
        public string? FilePath { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public readonly record struct SidebarTransferRequest(
        TransferDirection Direction,
        TransferKind Kind,
        string RemotePath);

    public partial class TerminalPane : UserControl, IDisposable
    {
        public ITerminalSession? Session { get; private set; }
        public TerminalBuffer? Buffer { get; private set; }
        public AnsiParser? Parser { get; private set; }
        public string ShellCommand { get; private set; } = string.Empty;
        public string ShellArgs { get; private set; } = string.Empty;
        public TerminalProfile? Profile { get; private set; }
        private Guid _paneId = Guid.NewGuid();

        /// <remarks>
        /// Session restore assigns a persisted id after construction
        /// (SessionManager.RestorePaneTree), i.e. after this pane already
        /// registered with the agent-session registry — so the setter re-keys
        /// the registry entry to keep it addressable under the current id.
        /// If re-keying fails (the entry stayed under the old id), the pane
        /// keeps the old id too: pane and registry must never disagree.
        /// </remarks>
        public Guid PaneId
        {
            get => _paneId;
            set
            {
                if (_paneId == value) return;
                var oldId = _paneId;
                if (NovaTerminal.AgentHost.AgentSessionRegistry.Instance.Rekey(oldId, value))
                {
                    _paneId = value;
                }
            }
        }

        public event Action<TerminalPane, SidebarTransferRequest>? RequestRemoteFilesSidebarTransfer;
        public event Action<bool>? RecordingStateChanged;
        public event Action<RecordingNotificationEventArgs>? RecordingNotification;
        public event Action<TerminalPane, string>? WorkingDirectoryChanged;
        public event Action<TerminalPane, string>? TitleChanged;
        public event Action<TerminalPane, PaneAction>? PaneActionRequested;
        public event Action<TerminalPane>? OutputReceived;
        public event Action<TerminalPane>? BellReceived;
        public event Action<TerminalPane>? CommandStarted;
        public event Action<TerminalPane, int?>? CommandFinished;
        public event Action<TerminalPane, int>? ProcessExited;
        /// <summary>A command ran at least <see cref="LongCommandNotificationPolicy.ThresholdSeconds"/>: (pane, command, exitCode, duration). Policy (setting, focus) is the window's call.</summary>
        public event Action<TerminalPane, string?, int?, TimeSpan>? LongCommandCompleted;

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;
        private bool _disposed;
        private NovaTerminal.AgentHost.AgentSessionRegistration? _agentRegistration;
        private DateTimeOffset? _lastCommandStartedAtUtc;
        private Action<int, int>? _onTermViewResize;
        private Action<float, float>? _onTermViewMetricsChanged;
        private Action<float, float>? _onTermViewMetricsLayout;
        private DispatcherTimer? _statusTimer;
        private bool _hasUserInteraction;
        private readonly SshDiagnosticsLevel _sshDiagnosticsLevel;
        private string? _pendingPasteFilePath;
        private string? _pendingEscapedPath;
        private CommandAssistController? _commandAssistController;
        private CommandAssistServices? _commandAssistServices;
        private ShellLifecycleTracker? _shellLifecycleTracker;

        /// <summary>
        /// True when <em>we</em> injected a bootstrap into this shell. Never true for SSH: the
        /// injection mechanisms (an <c>--rcfile</c> path, a <c>ZDOTDIR</c>/<c>XDG_CONFIG_HOME</c>
        /// override, a <c>-File</c> argument) all die at the SSH boundary.
        /// </summary>
        private bool _isShellIntegrationActive;

        /// <summary>
        /// True once this session has emitted any OSC 133 mark, whoever installed the thing that
        /// emits it. The runtime half of V2 Phase 2b's remote story: a remote host that sources the
        /// shipped snippet proves itself here rather than through
        /// <see cref="_isShellIntegrationActive"/>, which it can never set.
        /// </summary>
        /// <remarks>
        /// Written from the PTY read thread (the parser callbacks) and read from the UI thread
        /// (<see cref="UpdateCommandAssistContext"/>); <c>volatile</c> and bool-sized, so a reader
        /// sees either the old value or the new one. Latching - it is only ever set - so the
        /// double-set a concurrent first mark could produce is harmless, and the redundant context
        /// update it posts is idempotent.
        /// </remarks>
        private volatile bool _hasObservedShellIntegrationMark;
        private IReadOnlyDictionary<string, string>? _shellIntegrationEnvOverrides;
        private readonly OrderedAsyncEventDispatcher _shellIntegrationEventDispatcher = new();
        private readonly CommandAssistAnchorCalculator _commandAssistAnchorCalculator = new();

        // Newest OSC 133;B mark, written from the PTY read thread and read from the UI thread.
        // A ShellIntegrationMark is five fields wide, so a plain nullable field could be torn
        // across the two; the gate costs one uncontended lock per prompt.
        private readonly object _commandStartMarkGate = new();
        private ShellIntegrationMark? _latestCommandStartMark;

        // True between "the user pressed a key that edits the command line" and "the session sent
        // us bytes we have parsed into the grid". See NoteInputAwaitingEcho for why insertion
        // refuses while it is set. Written from the UI thread, cleared from the PTY read thread.
        private volatile bool _hasUnechoedInput;

        // Enter-time history capture for sessions with no OSC 133 marks (V2 Phase 1, task 7).
        // Never the query - see MarklessSubmissionAccumulator for why this is not the shadow
        // buffer coming back, and OnCommandAssistEnterObserved for how it composes with grid truth.
        private readonly MarklessSubmissionAccumulator _marklessSubmission = new();
        private string? _lastRelevantCommandText;
        private CommandAssistBarViewModel? _boundCommandAssistViewModel;
        private string? _lastCommandAssistAnchorDiagnosticSignature;
        private string? _lastCommandAssistAnchorAppliedSignature;
        private string? _lastCommandAssistAnchorCorrectionSignature;
        private bool _suppressSshAssistOverlayUntilSettled;

        // Last value of IsCommandAssistOverlayRendered the controller was told about. Starts false, which
        // matches the overlay host's IsVisible="False" in the XAML.
        private bool _wasCommandAssistOverlayRendered;
        private int _sshAssistCorrectionPassCount;
        private int _commandAssistPlacementCorrectionPasses;
        private readonly CommandAssistBubbleViewModel _hiddenCommandAssistBubbleViewModel = new() { IsVisible = false };
        private readonly CommandAssistPopupViewModel _hiddenCommandAssistPopupViewModel = new(new ObservableCollection<CommandAssistSuggestionItemViewModel>()) { IsVisible = false };
        private IRemoteDirectoryBrowserService _remoteDirectoryBrowserService = new RemoteDirectoryBrowserService();
        private RemoteFilesSidebarViewModel? _remoteFilesSidebarViewModel;
        private RemoteFilesSidebar? _remoteFilesSidebarHost;
        private bool _isRemoteFilesSidebarTestServiceConfigured;
        private string? _currentRecordingFilePath;
        private int _clipboardWriteAttemptsForTest;
        private const double CommandAssistBubbleWidth = 420;
        private const double CommandAssistBubbleHeight = 36;
        private const double CommandAssistPopupWidth = 520;
        private const double CommandAssistPopupHeight = 220;
        private const double CompactPopupWidthThreshold = 420;
        private const double CompactPopupHeightThreshold = 180;
        private const double ConservativeRemotePromptBandStartRatio = 0.55;
        private const int ConservativeRemoteMinVisibleRows = 8;
        private const double ConservativeRemoteShortPaneHeightThreshold = 300;
        private const int MaxSshAssistCorrectionPasses = 6;
        internal CommandAssistBarViewModel? CommandAssistViewModel => _commandAssistController?.ViewModel;

        /// <summary>
        /// The Command Assist dependency graph this pane uses. Assigned by <c>MainWindow.WirePane</c>,
        /// from the single instance built at the App composition root.
        /// </summary>
        /// <remarks>
        /// A property rather than a constructor parameter because this control has four public
        /// constructors plus two internal settings-carrying overloads, and panes are built from
        /// three different places (new tab, split, session restore); property injection at the one
        /// wiring funnel keeps all of them working without threading the graph through every
        /// signature. (It is not about XAML: <c>TerminalPane.axaml</c> only declares
        /// <c>x:Class</c> - no markup anywhere instantiates the type.) Not defaulted: a pane that
        /// reaches Command Assist initialization without one throws (see
        /// <c>RequireCommandAssistServices</c>) instead of quietly building a second graph, which is
        /// exactly the failure mode the removed static locator made invisible.
        /// </remarks>
        internal CommandAssistServices? CommandAssistServices
        {
            get => _commandAssistServices;
            set => _commandAssistServices = value;
        }

        public bool IsRecording => Session?.IsRecording ?? false;
        public string? CurrentWorkingDirectory { get; private set; }
        public string? CurrentOscTitle { get; private set; }
        public int? LastExitCode { get; private set; }
        public bool IsProcessRunning => Session?.IsProcessRunning ?? false;
        public bool HasActiveChildProcesses => Session?.HasActiveChildProcesses ?? false;
        public bool HasUserInteraction => _hasUserInteraction;

        private bool _isActivePane = false;
        public bool IsActivePane
        {
            get => _isActivePane;
            set
            {
                if (_isActivePane != value)
                {
                    _isActivePane = value;
                    UpdateFocusVisuals(IsKeyboardFocusWithin);
                    UpdateAgentSessionSnapshot();
                }
            }
        }

        /// <summary>
        /// Pushes this pane's current metadata into its agent-session
        /// registration (UI thread only). Cheap and allocation-light; called
        /// on title/cwd/profile/active-state changes so background registry
        /// readers never touch this control.
        /// </summary>
        private void UpdateAgentSessionSnapshot()
        {
            _agentRegistration?.UpdateSnapshot(
                GetBaseTabTitle(),
                Profile?.Name ?? "Terminal",
                Profile?.Type == ConnectionType.SSH ? "ssh" : "local",
                IsActivePane,
                Profile?.Id);
        }

        /// <summary>
        /// Pushes this pane's render inputs (cell metrics, font, shaping flags)
        /// into its agent-session registration so <c>captureScreen</c> can render
        /// the pane off the UI thread (A5). UI thread only; called whenever the
        /// font is re-measured or settings are applied.
        /// </summary>
        private void UpdateAgentRenderParameters()
        {
            if (_agentRegistration == null) return;

            _agentRegistration.UpdateRenderParameters(new NovaTerminal.Shell.PaneRenderParameters(
                TermView.Metrics,
                TermView.Typeface.FontFamily.Name,
                (float)TermView.FontSize,
                TermView.EnableLigatures,
                TermView.EnableComplexShaping));
        }

        public string GetBaseTabTitle()
        {
            if (!string.IsNullOrWhiteSpace(CurrentOscTitle))
            {
                return CurrentOscTitle!;
            }

            string profileName = Profile?.Name ?? "Terminal";
            if (!string.IsNullOrWhiteSpace(CurrentWorkingDirectory))
            {
                string normalized = CurrentWorkingDirectory!.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string leaf = System.IO.Path.GetFileName(normalized);
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    return $"{profileName} · {leaf}";
                }
            }

            return profileName;
        }

        public void ToggleRecording()
        {
            if (Session == null) return;

            string recordingsDirectory = AppPaths.RecordingsDirectory;

            if (Session.IsRecording)
            {
                Session.StopRecording();
                RecordingNotification?.Invoke(new RecordingNotificationEventArgs
                {
                    Kind = RecordingNotificationKind.Stopped,
                    IsRecording = Session.IsRecording,
                    FilePath = _currentRecordingFilePath,
                    RecordingsDirectory = recordingsDirectory
                });
                _currentRecordingFilePath = null;
            }
            else
            {
                try
                {
                    if (!System.IO.Directory.Exists(recordingsDirectory))
                    {
                        System.IO.Directory.CreateDirectory(recordingsDirectory);
                    }

                    string filename = BuildRecordingFileName(DateTime.Now, Guid.NewGuid().ToString("N"));
                    string path = System.IO.Path.Combine(recordingsDirectory, filename);

                    Session.StartRecording(path);
                    _currentRecordingFilePath = path;
                    RecordingNotification?.Invoke(new RecordingNotificationEventArgs
                    {
                        Kind = RecordingNotificationKind.Started,
                        IsRecording = Session.IsRecording,
                        FilePath = path,
                        RecordingsDirectory = recordingsDirectory
                    });
                }
                catch (Exception ex)
                {
                    _currentRecordingFilePath = null;
                    RecordingNotification?.Invoke(new RecordingNotificationEventArgs
                    {
                        Kind = RecordingNotificationKind.Failed,
                        IsRecording = Session.IsRecording,
                        FilePath = _currentRecordingFilePath,
                        RecordingsDirectory = recordingsDirectory,
                        ErrorMessage = ex.Message
                    });
                }
            }

            RecordingStateChanged?.Invoke(IsRecording);
        }

        internal static string BuildRecordingFileName(DateTime timestamp, string uniqueSuffix)
        {
            string normalizedSuffix = string.IsNullOrWhiteSpace(uniqueSuffix)
                ? Guid.NewGuid().ToString("N")
                : uniqueSuffix.Trim().ToLowerInvariant();

            string shortSuffix = normalizedSuffix.Length > 6
                ? normalizedSuffix[..6]
                : normalizedSuffix.PadRight(6, '0');

            return $"nova_rec_{timestamp:yyyyMMdd_HHmmss}_{shortSuffix}.rec";
        }

        public void UpdateProfile(TerminalProfile profile)
        {
            Profile = profile;
            UpdateAgentSessionSnapshot();
            TermView.ShellOverride = profile.ShellOverride;
            UpdateCommandAssistContext();
            UpdateRemoteFilesSidebarHostIdentity();
            if (!IsRemoteFilesSidebarSupported())
            {
                CloseRemoteFilesSidebar();
            }

            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        public Control ActiveControl => TermView;
        public ISshInteractionHandler? SshInteractionHandler { get; set; }

        public void ToggleRemoteFilesSidebar()
        {
            _ = ToggleRemoteFilesSidebarAsync();
        }

        public TerminalPane()
        {
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(null, null, c, r);
            SetupCommon(null);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        public TerminalPane(string shell)
            : this(shell, initialSettings: null)
        {
        }

        internal TerminalPane(string shell, TerminalSettings? initialSettings)
        {
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r);
            SetupCommon(initialSettings);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        public TerminalPane(string shell, string args)
            : this(shell, args, initialSettings: null)
        {
        }

        internal TerminalPane(string shell, string args, TerminalSettings? initialSettings)
        {
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r, args);
            SetupCommon(initialSettings);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        public TerminalPane(TerminalProfile profile)
            : this(profile, initialSettings: null, SshDiagnosticsLevel.None, useInitialSettings: false)
        {
        }

        public TerminalPane(TerminalProfile profile, SshDiagnosticsLevel sshDiagnosticsLevel)
            : this(profile, initialSettings: null, sshDiagnosticsLevel, useInitialSettings: false)
        {
        }

        internal TerminalPane(TerminalProfile profile, TerminalSettings initialSettings, SshDiagnosticsLevel sshDiagnosticsLevel = SshDiagnosticsLevel.None)
            : this(profile, initialSettings, sshDiagnosticsLevel, useInitialSettings: true)
        {
        }

        private TerminalPane(TerminalProfile profile, TerminalSettings? initialSettings, SshDiagnosticsLevel sshDiagnosticsLevel, bool useInitialSettings)
        {
            Profile = profile;
            InitializeComponent();
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterInitializeComponent");
            _sshDiagnosticsLevel = sshDiagnosticsLevel;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.ShellOverride = profile.ShellOverride;
            TermView.Ready += (c, r) => InitializeSession(profile.Command, profile, c, r);
            SetupCommon(useInitialSettings ? initialSettings : null);
            StartupPerformanceTracker.Current?.TryMarkCheckpoint("TerminalPane.Ctor.AfterSetupCommon");
        }

        private void SetupCommon(TerminalSettings? initialSettings)
        {
            // Agent-host observe surface (docs/agent-host/DIRECTION.md, A1):
            // inert bookkeeping until the IPC endpoint queries it. The
            // registration holds a lock-protected metadata snapshot (never a
            // live delegate into this control), pushed from the UI thread on
            // every relevant change; the entry is removed in DetachFromUiThread.
            _agentRegistration = new NovaTerminal.AgentHost.AgentSessionRegistration(
                PaneId,
                Buffer!,
                GetBaseTabTitle(),
                Profile?.Name ?? "Terminal",
                Profile?.Type == ConnectionType.SSH ? "ssh" : "local",
                IsActivePane,
                profileId: Profile?.Id);
            // A3 act: an agent typing into this pane is text the keyboard path never saw.
            _agentRegistration.InputInjected = NotifyExternalInputSent;
            NovaTerminal.AgentHost.AgentSessionRegistry.Instance.Register(_agentRegistration);
            TitleChanged += (_, _) => UpdateAgentSessionSnapshot();
            WorkingDirectoryChanged += (_, _) => UpdateAgentSessionSnapshot();

            // A2 status signals (docs/plans/2026-07-07-agent-host-a2-status-design.md):
            // PTY lifecycle events feed the per-session status machine. Command
            // lifecycle (started/finished) and prompt/accepted signals are wired
            // synchronously at the parser hooks in InitializeSession so their
            // relative order is preserved; alt-screen in HandleAltScreenChanged.
            OutputReceived += _ => _agentRegistration?.StatusMachine.NotifyOutput();
            BellReceived += _ => _agentRegistration?.StatusMachine.NotifyBell();
            ProcessExited += (_, exitCode) => _agentRegistration?.StatusMachine.NotifyExited(exitCode);

            TermView.KeyDownInterceptor = TryHandleCommandAssistKey;

            // Mouse support for the popup rows (V2 Phase 3a). Wired here rather than in
            // BindCommandAssistViews because the view is a fixed part of the pane's XAML tree while the
            // view-model comes and goes with the feature flag: subscribing on every bind would add a
            // second handler per rebind.
            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.SuggestionPointerSelected += OnCommandAssistSuggestionPointerSelected;
                CommandAssistPopup.SuggestionPointerAccepted += OnCommandAssistSuggestionPointerAccepted;
            }

            TermView.TextInput += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _hasUserInteraction = true;
                }
            };
            TermView.KeyDown += (_, e) =>
            {
                if (e.Key != Key.LeftShift &&
                    e.Key != Key.RightShift &&
                    e.Key != Key.LeftCtrl &&
                    e.Key != Key.RightCtrl &&
                    e.Key != Key.LeftAlt &&
                    e.Key != Key.RightAlt)
                {
                    _hasUserInteraction = true;
                }
            };

            // Wire up ScrollBar
            TermScrollBar.ValueChanged += ScrollBar_ValueChanged;

            TermView.ScrollStateChanged += (offset, max) =>
            {
                // Dispatch to UI thread to update ScrollBar value
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingScroll = true;
                    try
                    {
                        TermScrollBar.Maximum = max;
                        TermScrollBar.Value = max - offset;
                    }
                    finally
                    {
                        _isUpdatingScroll = false;
                    }
                }, DispatcherPriority.Render);
            };

            // Search UI
            SetupSearch();

            // Port Forwarding Status Timer
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (s, e) => UpdateForwardingStatus();
            _statusTimer.Start();

            // SFTP Status
            SftpService.Instance.JobUpdated += Sftp_JobUpdated;

            // Wire up focus syncing
            TermView.GotFocus += (s, e) => UpdateFocusVisuals(true);
            TermView.LostFocus += (s, e) => UpdateFocusVisuals(false);
            // Cached so DetachFromUiThread can remove it. As an uncached lambda it was the one
            // TermView handler left attached after disposal, which contradicted the claim that a
            // disposed pane stops reacting — harmless in practice (it only re-runs this pane's own
            // layout) but it made the invariant untestable, and an untestable invariant is one edit
            // away from being false (#102).
            _onTermViewMetricsLayout = (cw, ch) =>
            {
                UpdateMinimumSizeConstraints();
                UpdateCommandAssistOverlayPlacement();
            };
            TermView.MetricsChanged += _onTermViewMetricsLayout;
            TermView.CommandAssistAnchorHintChanged += () => UpdateCommandAssistOverlayPlacement();
            SizeChanged += (_, _) => UpdateCommandAssistOverlayPlacement();
            // Keystrokes are triggers, not content, for the *query* (V2 Phase 1c): Command Assist
            // re-reads the command line out of the grid on the refresh these queue, which is the
            // only source that also knows about the arrow keys, Ctrl+U, history recall and
            // shell-side Tab completion.
            //
            // They are content for one narrow purpose, added in V2 Phase 1 task 7: the markless
            // submission accumulator, which supplies Enter-time history capture for the sessions
            // the grid cannot serve. See NotifyTypedTextObserved and friends.
            TermView.TextInputObserved += NotifyTypedTextObserved;
            TermView.BackspaceObserved += NotifyBackspaceObserved;
            TermView.EnterObserved += OnCommandAssistEnterObserved;
            TermView.PasteObserved += NotifyPasteObserved;

            if (Buffer != null)
            {
                Buffer.OnScreenSwitched += OnBufferScreenSwitched;
            }

            // Load Settings
            ApplySettings(initialSettings ?? TerminalSettings.Load());
            UpdateMinimumSizeConstraints();
            AutomationProperties.SetName(TermView, "Terminal Pane");
            AutomationProperties.SetName(this, "Terminal Pane");

            // Smart Paste Action setup
            TermView.TextFileDropped += (s, args) =>
            {
                _pendingPasteFilePath = args.FilePath;
                _pendingEscapedPath = args.EscapedPath;
                string fileName = System.IO.Path.GetFileName(args.FilePath);
                // A notice riding along with the drop (currently only the WSL mapping
                // fallback) is appended rather than shown separately, because it would
                // otherwise overwrite this actionable prompt in the shared panel.
                ToastMessageText.Text = string.IsNullOrEmpty(args.Notice)
                    ? fileName
                    : $"{fileName} — {args.Notice}";
                // Restore the action buttons: an informational drop notice (below) hides
                // them, and the panel is shared between both uses.
                ToastPastePathBtn.IsVisible = true;
                ToastActionBtn.IsVisible = true;
                ToastPanel.IsVisible = true;
            };

            // Drop refused, or accepted with a caveat. Reuses the same panel - the message
            // belongs in the pane the drop landed on, not in a window-level toast - but with
            // the action buttons hidden, because there is nothing to act on: the text was
            // either never sent, or already sent.
            TermView.DropNotice += message =>
            {
                _pendingPasteFilePath = null;
                _pendingEscapedPath = null;
                ToastMessageText.Text = message;
                ToastPastePathBtn.IsVisible = false;
                ToastActionBtn.IsVisible = false;
                ToastPanel.IsVisible = true;
            };

            ToastCloseBtn.Click += (s, e) =>
            {
                ToastPanel.IsVisible = false;
                _pendingPasteFilePath = null;
                _pendingEscapedPath = null;
            };

            ToastPastePathBtn.Click += (s, e) =>
            {
                ToastPanel.IsVisible = false;
                if (!string.IsNullOrEmpty(_pendingEscapedPath) && Session != null)
                {
                    NotifyExternalInputSent();
                    Session.SendInput(_pendingEscapedPath);
                    _pendingPasteFilePath = null;
                    _pendingEscapedPath = null;
                }
            };

            ToastActionBtn.Click += async (s, e) =>
            {
                ToastPanel.IsVisible = false;
                if (!string.IsNullOrEmpty(_pendingPasteFilePath) && Session != null)
                {
                    try
                    {
                        string content = await System.IO.File.ReadAllTextAsync(_pendingPasteFilePath);
                        NotifyExternalInputSent();
                        NovaTerminal.Platform.Input.TerminalInputSender.SendBracketedPaste(Session, content);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to paste file contents: {ex.Message}");
                    }
                    _pendingPasteFilePath = null;
                    _pendingEscapedPath = null;
                }
            };

            // SFTP Context Menu
            var contextMenu = RootGrid.ContextMenu;
            if (contextMenu != null)
            {
                contextMenu.Opening += (_, _) => UpdatePaneContextMenuState();

                var paneMenu = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "Pane");
                if (paneMenu != null)
                {
                    foreach (var sub in paneMenu.Items.OfType<MenuItem>())
                    {
                        if (sub.Name == "MenuPaneSplitVertical") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.SplitVertical);
                        if (sub.Name == "MenuPaneSplitHorizontal") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.SplitHorizontal);
                        if (sub.Name == "MenuPaneEqualize") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.Equalize);
                        if (sub.Name == "MenuPaneToggleZoom") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.ToggleZoom);
                        if (sub.Name == "MenuPaneToggleBroadcast") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.ToggleBroadcast);
                        if (sub.Name == "MenuPaneClose") sub.Click += (s, e) => PaneActionRequested?.Invoke(this, PaneAction.Close);
                    }
                }

                var explainSelectionItem = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MenuExplainSelection");
                if (explainSelectionItem != null)
                {
                    explainSelectionItem.Click += async (_, _) => await ExplainSelectionAsync();
                }
            }

            InitializeRemoteFilesSidebar();
        }

        private void InitializeRemoteFilesSidebar()
        {
            SetRemoteFilesSidebarService(_remoteDirectoryBrowserService);

            if (MenuToggleRemoteFilesSidebar != null)
            {
                MenuToggleRemoteFilesSidebar.Click += async (_, _) => await ToggleRemoteFilesSidebarAsync();
            }

            UpdateRemoteFilesSidebarHostIdentity();
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private RemoteFilesSidebar EnsureRemoteFilesSidebarHost()
        {
            if (_remoteFilesSidebarHost != null)
            {
                return _remoteFilesSidebarHost;
            }

            var host = new RemoteFilesSidebar
            {
                IsVisible = false,
                DataContext = _remoteFilesSidebarViewModel
            };

            if (host.FindControl<Button>("BtnUploadFile") is Button uploadFileButton)
            {
                uploadFileButton.Click += (_, _) => RequestRemoteFilesSidebarUploadForCurrentDirectory(TransferKind.File);
            }

            if (host.FindControl<Button>("BtnUploadFolder") is Button uploadFolderButton)
            {
                uploadFolderButton.Click += (_, _) => RequestRemoteFilesSidebarUploadForCurrentDirectory(TransferKind.Folder);
            }

            if (host.FindControl<Button>("BtnDownloadSelected") is Button downloadSelectedButton)
            {
                downloadSelectedButton.Click += (_, _) => RequestRemoteFilesSidebarTransferForSelectedEntry();
            }

            if (RemoteFilesSidebarPresenter != null)
            {
                RemoteFilesSidebarPresenter.Content = host;
                RemoteFilesSidebarPresenter.IsVisible = false;
            }

            _remoteFilesSidebarHost = host;
            UpdateRemoteFilesSidebarHostIdentity();
            return host;
        }

        private void SetRemoteFilesSidebarService(IRemoteDirectoryBrowserService directoryBrowserService)
        {
            ArgumentNullException.ThrowIfNull(directoryBrowserService);

            if (_remoteFilesSidebarViewModel != null)
            {
                _remoteFilesSidebarViewModel.PropertyChanged -= OnRemoteFilesSidebarViewModelPropertyChanged;
            }

            _remoteDirectoryBrowserService = directoryBrowserService;
            _remoteFilesSidebarViewModel = new RemoteFilesSidebarViewModel(directoryBrowserService);
            _remoteFilesSidebarViewModel.PropertyChanged += OnRemoteFilesSidebarViewModelPropertyChanged;

            if (_remoteFilesSidebarHost != null)
            {
                _remoteFilesSidebarHost.DataContext = _remoteFilesSidebarViewModel;
            }

            UpdateRemoteFilesSidebarHostIdentity();
        }

        private void OnRemoteFilesSidebarViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private bool IsRemoteFilesSidebarSupported()
        {
            return Profile?.Type == ConnectionType.SSH &&
                   Profile.SshBackendKind == SshBackendKind.Native;
        }

        private void UpdateRemoteFilesSidebarEntryPointState()
        {
            if (MenuToggleRemoteFilesSidebar == null)
            {
                return;
            }

            bool isSupported = IsRemoteFilesSidebarSupported();
            bool isBlockedByAltScreen = Buffer?.IsAltScreenActive == true;
            bool isSidebarOpen = _remoteFilesSidebarViewModel?.IsOpen == true;
            bool isSidebarDisconnected = _remoteFilesSidebarViewModel?.IsDisconnected == true;
            bool canOpen = (isSidebarOpen && !isSidebarDisconnected) || Session?.IsProcessRunning == true;
            MenuToggleRemoteFilesSidebar.IsVisible = isSupported;
            MenuToggleRemoteFilesSidebar.IsEnabled = isSupported && !isBlockedByAltScreen && canOpen;
            MenuToggleRemoteFilesSidebar.Header = isSidebarOpen
                ? "Hide Remote Files"
                : "Remote Files";
        }

        private void UpdateRemoteFilesSidebarVisibility()
        {
            bool shouldShow =
                _remoteFilesSidebarViewModel?.IsOpen == true &&
                !(Buffer?.IsAltScreenActive ?? false) &&
                IsRemoteFilesSidebarSupported();

            if (shouldShow)
            {
                EnsureRemoteFilesSidebarHost().IsVisible = true;
            }
            else if (_remoteFilesSidebarHost != null)
            {
                _remoteFilesSidebarHost.IsVisible = false;
            }

            if (RemoteFilesSidebarPresenter != null)
            {
                RemoteFilesSidebarPresenter.IsVisible = shouldShow;
            }
        }

        private void UpdateRemoteFilesSidebarHostIdentity()
        {
            _remoteFilesSidebarHost?.SetHostIdentity(
                string.IsNullOrWhiteSpace(Profile?.Name) ? null : Profile.Name,
                BuildRemoteFilesSidebarSubtitle(Profile));
        }

        private static string? BuildRemoteFilesSidebarSubtitle(TerminalProfile? profile)
        {
            if (profile?.Type != ConnectionType.SSH)
            {
                return null;
            }

            string host = profile.SshHost?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            string subtitle = string.IsNullOrWhiteSpace(profile.SshUser)
                ? host
                : $"{profile.SshUser.Trim()}@{host}";

            if (profile.SshPort > 0 && profile.SshPort != 22)
            {
                subtitle = $"{subtitle}:{profile.SshPort}";
            }

            return subtitle;
        }

        private void UpdateRemoteFilesSidebarCurrentDirectoryState()
        {
            if (_remoteFilesSidebarViewModel == null)
            {
                return;
            }

            if (!_remoteFilesSidebarViewModel.IsOpen)
            {
                _remoteFilesSidebarViewModel.SetJumpToCurrentDirectoryCandidate(null);
                return;
            }

            string? currentWorkingDirectory = string.IsNullOrWhiteSpace(CurrentWorkingDirectory)
                ? null
                : CurrentWorkingDirectory.Trim();
            string? jumpTarget = string.Equals(
                currentWorkingDirectory,
                _remoteFilesSidebarViewModel.CurrentPath,
                StringComparison.Ordinal)
                ? null
                : currentWorkingDirectory;
            _remoteFilesSidebarViewModel.SetJumpToCurrentDirectoryCandidate(jumpTarget);
        }

        private async Task ToggleRemoteFilesSidebarAsync()
        {
            if (_remoteFilesSidebarViewModel == null)
            {
                return;
            }

            if (_remoteFilesSidebarViewModel.IsOpen)
            {
                CloseRemoteFilesSidebar();
                return;
            }

            if (!IsRemoteFilesSidebarSupported() ||
                Profile == null ||
                Session == null ||
                Session.Id == Guid.Empty ||
                Buffer?.IsAltScreenActive == true)
            {
                return;
            }

            await OpenRemoteFilesSidebarAsync(Profile.Id, Session.Id);
        }

        private async Task OpenRemoteFilesSidebarAsync(Guid profileId, Guid sessionId)
        {
            if (_remoteFilesSidebarViewModel == null)
            {
                return;
            }

            string startPath = RemoteSidebarStartPathResolver.Resolve(
                CurrentWorkingDirectory,
                Profile?.DefaultRemoteDir);
            await _remoteFilesSidebarViewModel.OpenAsync(
                profileId,
                sessionId,
                startPath,
                CancellationToken.None);
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private void CloseRemoteFilesSidebar()
        {
            _remoteFilesSidebarViewModel?.Close();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private void RequestRemoteFilesSidebarTransferForSelectedEntry()
        {
            if (_remoteFilesSidebarViewModel?.SelectedEntry is not { } selectedEntry)
            {
                return;
            }

            TransferKind kind = selectedEntry.IsDirectory
                ? TransferKind.Folder
                : TransferKind.File;
            RequestRemoteFilesSidebarTransfer?.Invoke(
                this,
                new SidebarTransferRequest(
                    TransferDirection.Download,
                    kind,
                    selectedEntry.FullPath));
        }

        private void RequestRemoteFilesSidebarUploadForCurrentDirectory(TransferKind kind)
        {
            string remoteDirectory = _remoteFilesSidebarViewModel?.CurrentPath
                ?? CurrentWorkingDirectory
                ?? Profile?.DefaultRemoteDir
                ?? "~";
            if (string.IsNullOrWhiteSpace(remoteDirectory))
            {
                return;
            }

            RequestRemoteFilesSidebarTransfer?.Invoke(
                this,
                new SidebarTransferRequest(
                    TransferDirection.Upload,
                    kind,
                    remoteDirectory));
        }

        private void InitializeCommandAssist()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                _commandAssistController?.Dismiss();
                ClearCommandAssistBindings();

                return;
            }

            if (_commandAssistController != null)
            {
                BindCommandAssistViews(_commandAssistController.ViewModel);

                _commandAssistController.HandleAltScreenChanged(Buffer?.IsAltScreenActive ?? false);
                UpdateCommandAssistContext();
                return;
            }

            TerminalSettings settings = _settings!;
            CommandAssistServices services = RequireCommandAssistServices();
            services.ApplyHistoryRetentionLimit(settings.CommandAssistMaxHistoryEntries);
            _commandAssistController = new CommandAssistController(
                services.HistoryStore,
                services.SecretsFilter,
                services.SuggestionEngine,
                services.SnippetStore,
                services.CommandDocsProvider,
                services.RecipeProvider,
                services.ErrorInsightService,
                modeRouter: null,
                resultBuilder: null,
                // The grid-truth seam. Command Assist may not reference NovaTerminal.VT (see
                // ProjectFileLayeringTests), so the reader's GridCommandLine is mapped to the
                // assist assembly's own AssistQuerySnapshot right here, at the one boundary that
                // can see both types. Everything downstream sees plain data.
                queryProvider: TryReadAssistQuerySnapshot,

                // The other seam the controller cannot see for itself: whether the overlay it believes
                // is up is actually on screen. This pane hides it (no layout) and dims it (placement
                // correction) on its own authority, and an armed Enter on a zero-pixel surface is the
                // PR #290 review's first blocker.
                renderedSurfaceProbe: () => IsCommandAssistOverlayRendered,
                dispatch: action =>
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(action);
                    }
                });

            BindCommandAssistViews(_commandAssistController.ViewModel);

            _commandAssistController.HandleAltScreenChanged(Buffer?.IsAltScreenActive ?? false);
            UpdateCommandAssistContext();
        }

        /// <summary>
        /// Returns the injected Command Assist graph, or throws describing who was supposed to
        /// supply it.
        /// </summary>
        private CommandAssistServices RequireCommandAssistServices()
        {
            return _commandAssistServices ?? throw new InvalidOperationException(
                "TerminalPane.CommandAssistServices was not assigned before Command Assist " +
                "initialized. MainWindow.WirePane injects the instance built by AppServices at the " +
                "App composition root; a pane created outside that path must set it explicitly.");
        }

        private void BindCommandAssistViews(CommandAssistBarViewModel? viewModel)
        {
            if (!ReferenceEquals(_boundCommandAssistViewModel, viewModel))
            {
                if (_boundCommandAssistViewModel != null)
                {
                    _boundCommandAssistViewModel.PropertyChanged -= OnCommandAssistViewModelPropertyChanged;
                }

                _boundCommandAssistViewModel = viewModel;

                if (_boundCommandAssistViewModel != null)
                {
                    _boundCommandAssistViewModel.PropertyChanged += OnCommandAssistViewModelPropertyChanged;
                }
            }

            if (CommandAssistBubble != null)
            {
                CommandAssistBubble.DataContext = viewModel?.Bubble;
            }

            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.DataContext = viewModel?.Popup;
            }

            UpdateCommandAssistOverlayPlacement();
        }

        private void ClearCommandAssistBindings()
        {
            if (_boundCommandAssistViewModel != null)
            {
                _boundCommandAssistViewModel.PropertyChanged -= OnCommandAssistViewModelPropertyChanged;
                _boundCommandAssistViewModel = null;
            }

            if (CommandAssistBubble != null)
            {
                CommandAssistBubble.DataContext = _hiddenCommandAssistBubbleViewModel;
            }

            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.DataContext = _hiddenCommandAssistPopupViewModel;
            }
        }

        private bool IsCommandAssistFeatureEnabled()
        {
            return _settings?.CommandAssistEnabled == true &&
                   _settings.CommandAssistHistoryEnabled;
        }

        // When this returns true the controller is guaranteed non-null; the attribute lets
        // the compiler's null-flow analysis see that, so callers can dereference
        // _commandAssistController directly after the guard without CS8602.
        [MemberNotNullWhen(true, nameof(_commandAssistController))]
        private bool EnsureCommandAssistInitialized()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            if (_commandAssistController == null)
            {
                InitializeCommandAssist();
            }

            return _commandAssistController != null;
        }

        private void OnCommandAssistViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateCommandAssistOverlayPlacement();
        }

        internal CommandAssistAnchorLayout? CalculateCommandAssistAnchorLayoutForTest()
        {
            return TryCalculateCommandAssistAnchorLayout();
        }

        private CommandAssistAnchorLayout? TryCalculateCommandAssistAnchorLayout()
        {
            // During startup (especially SSH), TermView bounds can briefly report a partial height.
            // Anchor against the host pane bounds first so overlays don't jump to the top band.
            double paneWidth = Bounds.Width > 0 ? Bounds.Width : TermView.Bounds.Width;
            double paneHeight = Bounds.Height > 0 ? Bounds.Height : TermView.Bounds.Height;
            if (paneWidth <= 0 || paneHeight <= 0)
            {
                return null;
            }

            CommandAssistPromptHint? promptHint = TermView.GetCommandAssistPromptHint();
            CommandAssistMarkAnchorHint? markHint = TryGetCommandAssistMarkAnchorHint();
            bool hasMarkAnchor = markHint.HasValue;
            float fallbackCellHeight = TermView.Metrics.CellHeight > 0 ? TermView.Metrics.CellHeight : 18;
            int fallbackVisibleRows = TermView.Rows > 0 ? TermView.Rows : 1;
            CommandAssistSurfaceSizing sizing = CalculateCommandAssistSurfaceSizing(paneWidth, paneHeight);
            bool hasReliablePromptAnchor = IsCommandAssistPromptAnchorReliable(promptHint, hasMarkAnchor);
            float anchorCellHeight = markHint?.CellHeight ?? promptHint?.CellHeight ?? fallbackCellHeight;
            int hintCursorRow = promptHint?.VisibleCursorVisualRow ?? 0;
            int hintVisibleRows = markHint?.VisibleRows ?? promptHint?.VisibleRows ?? fallbackVisibleRows;
            int paneEstimatedVisibleRows = anchorCellHeight > 0
                ? Math.Max(1, (int)Math.Floor(paneHeight / anchorCellHeight))
                : hintVisibleRows;
            // Pane-estimated rows are a startup-jitter workaround for the heuristic path only: the
            // hint's row count lags the pane's real height for a few frames over SSH. A mark hint
            // reports the row count its own row was resolved against, so overriding it would move
            // the mark relative to a viewport it was never measured in.
            bool shouldUsePaneEstimatedRows = Profile?.Type == ConnectionType.SSH &&
                                              !hasMarkAnchor &&
                                              !hasReliablePromptAnchor &&
                                              paneEstimatedVisibleRows > hintVisibleRows;
            int anchorVisibleRows = shouldUsePaneEstimatedRows ? paneEstimatedVisibleRows : hintVisibleRows;
            int anchorCursorRow = Math.Clamp(hintCursorRow, 0, Math.Max(0, anchorVisibleRows - 1));
            int anchorMarkRow = hasMarkAnchor
                ? Math.Clamp(markHint!.Value.VisibleMarkVisualRow, 0, Math.Max(0, anchorVisibleRows - 1))
                : -1;
            bool shouldSuppress = ShouldSuppressConservativeRemoteAssist(promptHint, hasReliablePromptAnchor, hasMarkAnchor, paneHeight);
            if (shouldSuppress)
            {
                LogCommandAssistAnchorDiagnostics(
                    paneWidth,
                    paneHeight,
                    hasReliablePromptAnchor,
                    hasMarkAnchor,
                    anchorMarkRow,
                    promptHint,
                    anchorCellHeight,
                    anchorCursorRow,
                    anchorVisibleRows,
                    shouldSuppress,
                    layout: null);
                return null;
            }

            CommandAssistAnchorLayout layout = _commandAssistAnchorCalculator.Calculate(new CommandAssistAnchorRequest(
                PaneWidth: paneWidth,
                PaneHeight: paneHeight,
                CellHeight: anchorCellHeight,
                CursorVisualRow: anchorCursorRow,
                VisibleRows: anchorVisibleRows,
                BubbleWidth: sizing.BubbleWidth,
                BubbleHeight: sizing.BubbleHeight,
                PopupWidth: sizing.PopupWidth,
                PopupHeight: sizing.PopupHeight,
                HasReliablePromptAnchor: hasReliablePromptAnchor,
                HasMarkAnchor: hasMarkAnchor,
                MarkVisualRow: anchorMarkRow));
            LogCommandAssistAnchorDiagnostics(
                paneWidth,
                paneHeight,
                hasReliablePromptAnchor,
                hasMarkAnchor,
                anchorMarkRow,
                promptHint,
                anchorCellHeight,
                anchorCursorRow,
                anchorVisibleRows,
                shouldSuppress,
                layout);
            return layout;
        }

        /// <summary>
        /// The newest <c>OSC 133;B</c> mark resolved to a viewport row, or <c>null</c> when there is
        /// no live mark or it is not on screen.
        /// </summary>
        /// <remarks>
        /// Re-read on every placement pass rather than cached: the answer changes with the scroll
        /// offset (<see cref="TerminalView.CommandAssistAnchorHintChanged"/> fires on scroll), and the
        /// mark itself is replaced on every prompt repaint and dropped on <c>OSC 133;D</c>.
        /// </remarks>
        private CommandAssistMarkAnchorHint? TryGetCommandAssistMarkAnchorHint()
        {
            ShellIntegrationMark? mark;
            lock (_commandStartMarkGate)
            {
                mark = _latestCommandStartMark;
            }

            return mark is ShellIntegrationMark live
                ? TermView.GetCommandAssistMarkAnchorHint(live)
                : null;
        }

        private void LogCommandAssistAnchorDiagnostics(
            double paneWidth,
            double paneHeight,
            bool hasReliablePromptAnchor,
            bool hasMarkAnchor,
            int anchorMarkRow,
            CommandAssistPromptHint? promptHint,
            float anchorCellHeight,
            int anchorCursorRow,
            int anchorVisibleRows,
            bool shouldSuppress,
            CommandAssistAnchorLayout? layout)
        {
            if (Profile?.Type != ConnectionType.SSH)
            {
                return;
            }

            int hintCursorRow = promptHint?.VisibleCursorVisualRow ?? -1;
            int hintVisibleRows = promptHint?.VisibleRows ?? -1;
            string layoutState = layout == null
                ? "none"
                : $"bubbleY={layout.BubbleRect.Y:F0},bubbleBottom={layout.BubbleRect.Bottom:F0},promptY={layout.PromptRect.Y:F0},usesPrompt={layout.UsesPromptAnchor},usesMark={layout.UsesMarkAnchor}";
            string signature =
                $"pw={paneWidth:F0},ph={paneHeight:F0},tw={TermView.Bounds.Width:F0},th={TermView.Bounds.Height:F0},rel={hasReliablePromptAnchor},mark={hasMarkAnchor},markRow={anchorMarkRow},sup={shouldSuppress},hintRow={hintCursorRow},hintRows={hintVisibleRows},cell={anchorCellHeight:F1},anchorRow={anchorCursorRow},anchorRows={anchorVisibleRows},vmVis={_boundCommandAssistViewModel?.IsVisible == true},{layoutState}";
            if (string.Equals(signature, _lastCommandAssistAnchorDiagnosticSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastCommandAssistAnchorDiagnosticSignature = signature;
            TerminalLogger.Log($"[AssistAnchor][SSH] {signature}");
        }

        /// <summary>
        /// Hides the overlay entirely on short SSH panes whose prompt is still high in the pane —
        /// the "it might be a login banner" case.
        /// </summary>
        /// <remarks>
        /// <para>
        /// V2 Phase 2a: this is a hedge against not knowing where the prompt is, so a mark-anchored
        /// pane never reaches it. The <paramref name="hasMarkAnchor"/> early-out is redundant with
        /// <paramref name="hasReliablePromptAnchor"/> (a mark makes the anchor reliable by
        /// definition) and deliberately so: the property under test is "marks bypass suppression",
        /// and it should not depend on a second flag staying in sync.
        /// </para>
        /// <para>
        /// V2 Phase 3a adds the second bypass, and it is the fix for the owner's third report: in a tab
        /// split into two SSH panes, the assist did not appear on one of them at all. A split halves the
        /// pane height, which puts both panes under
        /// <see cref="ConservativeRemoteShortPaneHeightThreshold"/>, and on the pane whose prompt was
        /// still in the upper band this returned true — for <c>Ctrl+R</c> as readily as for a passive
        /// bubble. Suppressing a surface the user summoned is not conservative behavior; it is the
        /// feature not working. So an explicitly requested surface is never hidden here, and the worst
        /// case becomes what the anchor calculator already does without a reliable anchor: the safe
        /// lower band.
        /// </para>
        /// <para>
        /// The suppression stays exactly as it was for uninvited surfaces on markless SSH, which is the
        /// case it was written for.
        /// </para>
        /// </remarks>
        private bool ShouldSuppressConservativeRemoteAssist(
            CommandAssistPromptHint? promptHint,
            bool hasReliablePromptAnchor,
            bool hasMarkAnchor,
            double paneHeight)
        {
            if (hasMarkAnchor || IsCommandAssistSurfaceUserRequested)
            {
                return false;
            }

            if (Profile?.Type != ConnectionType.SSH || hasReliablePromptAnchor || paneHeight > ConservativeRemoteShortPaneHeightThreshold)
            {
                return false;
            }

            if (!promptHint.HasValue)
            {
                return true;
            }

            if (promptHint.Value.VisibleRows < ConservativeRemoteMinVisibleRows)
            {
                return true;
            }

            double normalizedCursorRow = promptHint.Value.VisibleCursorVisualRow / (double)Math.Max(1, promptHint.Value.VisibleRows - 1);
            return normalizedCursorRow < ConservativeRemotePromptBandStartRatio;
        }

        /// <summary>
        /// Whether the anchor row may be trusted for prompt-adjacent placement.
        /// </summary>
        /// <remarks>
        /// V2 Phase 2a changed this from a per-session-type guess to a per-prompt fact where one is
        /// available: a live <c>OSC 133;B</c> mark in the viewport says where the prompt <i>is</i>,
        /// and that is equally true over SSH — an instrumented remote emits the same marks a local
        /// shell does. Only markless sessions fall through to the old rule, which is why the SSH
        /// clause below survives rather than being deleted.
        /// </remarks>
        private bool IsCommandAssistPromptAnchorReliable(CommandAssistPromptHint? promptHint, bool hasMarkAnchor)
        {
            if (hasMarkAnchor)
            {
                return true;
            }

            if (!promptHint.HasValue)
            {
                return false;
            }

            // Markless SSH sessions stay on the heuristic path, so cursor-row hints are not
            // trustworthy enough for prompt-adjacent anchoring.
            if (Profile?.Type == ConnectionType.SSH)
            {
                return false;
            }

            return true;
        }

        private static CommandAssistSurfaceSizing CalculateCommandAssistSurfaceSizing(double paneWidth, double paneHeight)
        {
            double bubbleWidth = Math.Clamp(paneWidth * 0.44, 280, CommandAssistBubbleWidth);
            double popupWidth = Math.Clamp(paneWidth * 0.58, 360, CommandAssistPopupWidth);
            double popupHeight = Math.Clamp(paneHeight * 0.45, 160, CommandAssistPopupHeight);

            return new CommandAssistSurfaceSizing(
                BubbleWidth: bubbleWidth,
                BubbleHeight: CommandAssistBubbleHeight,
                PopupWidth: popupWidth,
                PopupHeight: popupHeight);
        }

        private void UpdateCommandAssistOverlayPlacement()
        {
            CommandAssistAnchorLayout? layout = TryCalculateCommandAssistAnchorLayout();
            bool shouldShowOverlayHost = layout != null && (_boundCommandAssistViewModel?.IsVisible == true);
            if (!shouldShowOverlayHost || layout?.UsesMarkAnchor == true)
            {
                // Mark-anchored placement never hides the overlay while it settles: there is nothing
                // to settle. Clearing here also unwinds any suppression left over from a markless
                // frame earlier in the same session.
                //
                // A user-requested surface deliberately does *not* clear the counters here, even though
                // it is also never hidden (V2 Phase 3a). Resetting _sshAssistCorrectionPassCount on
                // every placement pass would stop MaxSshAssistCorrectionPasses from ever being reached,
                // and since a correction pass posts another placement pass, that is an unbounded render
                // loop. The bypass therefore lives on the opacity write below, which is the only thing
                // the user can see anyway.
                _suppressSshAssistOverlayUntilSettled = false;
                _sshAssistCorrectionPassCount = 0;

                // The correction-log dedup signature belongs to the run of passes being abandoned.
                // Left set, the first [Corrected] line after a later markless relapse is swallowed
                // as a duplicate of one from before the transition - and that first line is exactly
                // the diagnostic that says the mark anchor stopped working.
                _lastCommandAssistAnchorCorrectionSignature = null;
            }

            if (CommandAssistOverlayHost != null)
            {
                // The settle-suppression never applies to a surface the user asked for (V2 Phase 3a):
                // an invisible answer to Ctrl+R is indistinguishable from no answer, which is how the
                // owner experienced it on one pane of an SSH split.
                bool keepOverlayOpaque = !_suppressSshAssistOverlayUntilSettled || IsCommandAssistSurfaceUserRequested;
                CommandAssistOverlayHost.IsVisible = shouldShowOverlayHost;
                CommandAssistOverlayHost.Opacity = shouldShowOverlayHost && keepOverlayOpaque ? 1.0 : 0.0;
                NotifyCommandAssistOverlayRenderedChanged();
            }

            if (layout == null)
            {
                return;
            }

            if (CommandAssistBubble != null)
            {
                if (_boundCommandAssistViewModel != null)
                {
                    _boundCommandAssistViewModel.Bubble.ShowQueryText = !layout.UseCompactBubbleLayout;

                    // Same rule, same reason, one more casualty of a 280 px bubble: the hint strip's Auto
                    // column beat the summary's * column at the width a split SSH pane produces, so the
                    // one thing the bubble exists to show was squeezed out by a legend for it
                    // (PR #290 review). The popup footer still carries the shortcuts.
                    _boundCommandAssistViewModel.Bubble.ShowShortcutHint = !layout.UseCompactBubbleLayout;
                }

                CommandAssistBubble.Width = layout.BubbleRect.Width;
                CommandAssistBubble.Height = layout.BubbleRect.Height;
                CommandAssistBubble.MinHeight = layout.BubbleRect.Height;
                CommandAssistBubble.MaxWidth = layout.BubbleRect.Width;
                CommandAssistBubble.MaxHeight = layout.BubbleRect.Height;
                CommandAssistBubble.Margin = new Thickness(
                    layout.BubbleRect.X,
                    layout.BubbleRect.Y,
                    0,
                    0);
            }

            if (CommandAssistPopup != null)
            {
                if (_boundCommandAssistViewModel != null)
                {
                    _boundCommandAssistViewModel.Popup.UseCompactLayout =
                        layout.PopupRect.Width <= CompactPopupWidthThreshold ||
                        layout.PopupRect.Height <= CompactPopupHeightThreshold;
                }

                CommandAssistPopup.Width = layout.PopupRect.Width;
                CommandAssistPopup.Height = layout.PopupRect.Height;
                CommandAssistPopup.MinHeight = layout.PopupRect.Height;
                CommandAssistPopup.MaxWidth = layout.PopupRect.Width;
                CommandAssistPopup.MaxHeight = layout.PopupRect.Height;
                CommandAssistPopup.Margin = new Thickness(
                    layout.PopupRect.X,
                    layout.PopupRect.Y,
                    0,
                    0);
            }

            LogCommandAssistAnchorAppliedDiagnostics(layout);
            ScheduleCommandAssistPlacementCorrection(layout);
        }

        private void LogCommandAssistAnchorAppliedDiagnostics(CommandAssistAnchorLayout layout)
        {
            if (Profile?.Type != ConnectionType.SSH || CommandAssistBubble == null)
            {
                return;
            }

            string signature =
                $"layoutY={layout.BubbleRect.Y:F0},layoutPromptY={layout.PromptRect.Y:F0},appliedBubbleTop={CommandAssistBubble.Margin.Top:F0},appliedBubbleVis={CommandAssistBubble.IsVisible},hostVis={CommandAssistOverlayHost?.IsVisible == true},vmVis={_boundCommandAssistViewModel?.IsVisible == true},popupVm={_boundCommandAssistViewModel?.IsPopupOpen == true}";
            if (string.Equals(signature, _lastCommandAssistAnchorAppliedSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastCommandAssistAnchorAppliedSignature = signature;
            TerminalLogger.Log($"[AssistAnchor][SSH][Applied] {signature}");
        }

        /// <summary>
        /// Total placement-correction passes this pane has scheduled, ever. A test seam for the
        /// V2 Phase 2a property "a mark-anchored pane runs zero correction passes" — the production
        /// evidence for the same thing is the absence of <c>[AssistAnchor][SSH][Corrected]</c> lines
        /// in the log, which a test cannot read.
        /// </summary>
        /// <remarks>
        /// Zero is also what an under-driven test reads: the counter is only reachable with a visible
        /// bound view model on an SSH pane. <c>CommandAssistLayoutTests</c> pairs every zero-pass
        /// assertion with a markless negative control that must reach it, because without one the
        /// assertion holds with the <c>UsesMarkAnchor</c> gate deleted.
        /// </remarks>
        internal int CommandAssistPlacementCorrectionPassesForTest => _commandAssistPlacementCorrectionPasses;

        /// <summary>
        /// Whether a computed layout warrants a placement-correction pass.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="ScheduleCommandAssistPlacementCorrection"/> so the V2 Phase 2a
        /// rule — "a mark anchor runs no corrections, whatever the session type" — is assertable
        /// without a visible assist view model and a live render pass driving it.
        /// </remarks>
        private bool ShouldCorrectCommandAssistPlacement(CommandAssistAnchorLayout layout, bool assistIsVisible)
        {
            if (layout.UsesMarkAnchor)
            {
                return false;
            }

            // Deliberately *not* gated on IsCommandAssistSurfaceUserRequested. V2 Phase 3a's rule is
            // that a summoned surface is never hidden, not that it is never corrected: correcting the
            // placement is the useful half of this stack and costs the user nothing. What it must not do
            // is drop the overlay to zero opacity while it settles, which for up to six render passes
            // reads as "Ctrl+R did nothing" — so the hiding is what carries the bypass, at the two
            // places that apply it (see the opacity write in UpdateCommandAssistOverlayPlacement and
            // the suppression write inside CorrectPlacement).
            return Profile?.Type == ConnectionType.SSH && assistIsVisible;
        }

        /// <summary>
        /// Whether the assist surface currently on screen is one the user asked for by name —
        /// <c>Ctrl+Space</c>, <c>Ctrl+R</c>, Help, a confident Fix popup.
        /// </summary>
        /// <remarks>
        /// The single question every overlay-hiding heuristic in this file now asks first. The
        /// authority is <c>AssistSessionStateMachine.IsUserRequestedSurface</c>; this is only the
        /// null-safe pane-side spelling of it.
        /// </remarks>
        private bool IsCommandAssistSurfaceUserRequested =>
            _commandAssistController?.IsUserRequestedSurface == true;

        /// <summary>
        /// Whether the assist overlay this pane hosts is actually on screen right now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pane-side half of the PR #290 review's first blocker. Assist visibility has two
        /// authorities and they disagree: the session says "a surface is up" through
        /// <c>CommandAssistBarViewModel.IsVisible</c>, and this pane independently hides the overlay host
        /// when the conservative anchor check yields no layout, or drops it to zero opacity while a
        /// placement correction settles. Both of those bypasses are waived for a surface the user asked
        /// for by name - and a <c>PassivePopup</c> is not one, so a passive popup could hold an armed
        /// <c>Enter</c> at zero pixels while the user's command line silently failed to submit.
        /// </para>
        /// <para>
        /// Read live rather than cached: it is asked on the key path and during
        /// <c>SyncPresentationState</c>, and both want the current frame's answer.
        /// </para>
        /// </remarks>
        internal bool IsCommandAssistOverlayRendered =>
            CommandAssistOverlayHost is { IsVisible: true } host && host.Opacity > 0;

        /// <summary>
        /// Tells the controller the answer to <see cref="IsCommandAssistOverlayRendered"/> moved, so the
        /// hint strip can catch up with the routing decision.
        /// </summary>
        /// <remarks>
        /// Gated on an actual change, and not only to save work: republishing presentation state can
        /// raise <c>IsAcceptOnEnterArmed</c>, which this pane listens to, which runs another placement
        /// pass. The change check is what makes that converge on the second pass instead of recursing.
        /// </remarks>
        private void NotifyCommandAssistOverlayRenderedChanged()
        {
            bool isRendered = IsCommandAssistOverlayRendered;
            if (isRendered == _wasCommandAssistOverlayRendered)
            {
                return;
            }

            _wasCommandAssistOverlayRendered = isRendered;
            _commandAssistController?.NotifyRenderedSurfaceVisibilityChanged();
        }

        internal bool ShouldCorrectCommandAssistPlacementForTest(CommandAssistAnchorLayout layout, bool assistIsVisible)
        {
            return ShouldCorrectCommandAssistPlacement(layout, assistIsVisible);
        }

        /// <summary>
        /// Re-applies the computed margins on the next render pass when the rendered position drifted
        /// from the computed one, hiding the overlay until it agrees.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the SSH jitter mitigation from the 2026-03-11 firefight: the heuristic anchor moved
        /// between frames during remote startup, so the overlay was chasing a row that had already
        /// changed, visibly.
        /// </para>
        /// <para>
        /// V2 Phase 2a gates it off for mark-anchored layouts rather than deleting it. A mark row does
        /// not jitter — it is re-derived from the buffer on every pass and is either on screen or
        /// absent — so there is nothing for a correction pass to correct, and the opacity flicker it
        /// costs is pure loss. The stack stays for markless SSH, which is still a supported session
        /// type; it goes away with that, not with this change.
        /// </para>
        /// </remarks>
        private void ScheduleCommandAssistPlacementCorrection(CommandAssistAnchorLayout layout)
        {
            if (!ShouldCorrectCommandAssistPlacement(layout, _boundCommandAssistViewModel?.IsVisible == true))
            {
                return;
            }

            _commandAssistPlacementCorrectionPasses++;

            void CorrectPlacement()
            {
                if (CommandAssistBubble == null || CommandAssistOverlayHost == null || !CommandAssistOverlayHost.IsVisible)
                {
                    return;
                }

                // `layout` was captured a frame ago, while the pane was still markless. A 133;B mark
                // can land in between - that is the markless->mark handoff this change exists to
                // clean up - and by now the overlay has been re-placed against the mark row. Measured
                // against the stale layout that reads as drift, so the block below would hide the
                // overlay and re-apply markless margins for a frame at the exact moment the anchor
                // became exact. Re-derive and re-ask the gate instead; the answer for a mark-anchored
                // pane is "no correction", so this returns without touching anything.
                CommandAssistAnchorLayout? currentLayout = TryCalculateCommandAssistAnchorLayout();
                if (currentLayout == null ||
                    !ShouldCorrectCommandAssistPlacement(currentLayout, _boundCommandAssistViewModel?.IsVisible == true))
                {
                    // Not a bare return: a markless pass may have left the overlay hidden waiting for
                    // this pass to clear it, and nothing else will now.
                    ReleaseSshAssistOverlaySuppression();
                    return;
                }

                Control? anchorControl = CommandAssistBubble.IsVisible
                    ? CommandAssistBubble
                    : CommandAssistPopup != null && CommandAssistPopup.IsVisible
                        ? CommandAssistPopup
                        : null;
                if (anchorControl == null)
                {
                    return;
                }

                Point? anchorTopLeft = anchorControl.TranslatePoint(new Point(0, 0), this);
                if (!anchorTopLeft.HasValue)
                {
                    return;
                }

                bool anchoredToBubble = ReferenceEquals(anchorControl, CommandAssistBubble);
                double expectedTop = anchoredToBubble ? layout.BubbleRect.Y : layout.PopupRect.Y;
                double actualTop = anchorTopLeft.Value.Y;
                double drift = Math.Abs(actualTop - expectedTop);
                if (drift <= 2)
                {
                    ReleaseSshAssistOverlaySuppression();
                    return;
                }

                // Correct the placement either way; hide only what the user did not ask for.
                if (!IsCommandAssistSurfaceUserRequested)
                {
                    _suppressSshAssistOverlayUntilSettled = true;
                    CommandAssistOverlayHost.Opacity = 0.0;
                    NotifyCommandAssistOverlayRenderedChanged();
                }

                // Re-apply anchored margins if the rendered position drifted from expected.
                CommandAssistBubble.Margin = new Thickness(layout.BubbleRect.X, layout.BubbleRect.Y, 0, 0);
                if (CommandAssistPopup != null)
                {
                    CommandAssistPopup.Margin = new Thickness(layout.PopupRect.X, layout.PopupRect.Y, 0, 0);
                }

                string signature = $"anchor={(anchoredToBubble ? "bubble" : "popup")},expected={expectedTop:F0},actual={actualTop:F0},drift={drift:F0},pass={_sshAssistCorrectionPassCount}";
                if (!string.Equals(signature, _lastCommandAssistAnchorCorrectionSignature, StringComparison.Ordinal))
                {
                    _lastCommandAssistAnchorCorrectionSignature = signature;
                    TerminalLogger.Log($"[AssistAnchor][SSH][Corrected] {signature}");
                }

                if (_sshAssistCorrectionPassCount >= MaxSshAssistCorrectionPasses)
                {
                    _suppressSshAssistOverlayUntilSettled = false;
                    _sshAssistCorrectionPassCount = 0;
                    CommandAssistOverlayHost.Opacity = 1.0;
                    NotifyCommandAssistOverlayRenderedChanged();
                    TerminalLogger.Log("[AssistAnchor][SSH][Corrected] max-pass reached; showing overlay with best-known anchor.");
                    return;
                }

                _sshAssistCorrectionPassCount++;

                // Re-evaluate on the next render pass; keep host hidden until settled.
                Dispatcher.UIThread.Post(UpdateCommandAssistOverlayPlacement, DispatcherPriority.Render);
            }

            Dispatcher.UIThread.Post(CorrectPlacement, DispatcherPriority.Render);
        }

        /// <summary>
        /// Ends a run of placement-correction passes and un-hides the overlay if one of them hid it.
        /// </summary>
        /// <remarks>
        /// The correction stack is the only thing that sets
        /// <see cref="_suppressSshAssistOverlayUntilSettled"/>, so it is also the only thing that can
        /// clear it: every exit from a correction pass that is not "post another one" comes through
        /// here, or the overlay stays at zero opacity until the next placement pass happens to run.
        /// </remarks>
        private void ReleaseSshAssistOverlaySuppression()
        {
            _sshAssistCorrectionPassCount = 0;
            if (!_suppressSshAssistOverlayUntilSettled)
            {
                return;
            }

            _suppressSshAssistOverlayUntilSettled = false;
            if (CommandAssistOverlayHost != null)
            {
                CommandAssistOverlayHost.Opacity = 1.0;
                NotifyCommandAssistOverlayRenderedChanged();
            }
        }

        private void OnBufferScreenSwitched(bool isAltScreen)
        {
            Dispatcher.UIThread.Post(() => HandleAltScreenChanged(isAltScreen));
        }

        private void HandleAltScreenChanged(bool isAltScreen)
        {
            // A full-screen app owns the keyboard, and the keys it eats are not line edits. Drop
            // whatever half-line was pending on the way in so a TUI session cannot leave text
            // behind that a later Enter captures, and drop it again on the way out because the
            // prompt underneath was redrawn by something the accumulator never saw.
            _marklessSubmission.Reset();

            _agentRegistration?.StatusMachine.NotifyAltScreenChanged(isAltScreen);
            _commandAssistController?.HandleAltScreenChanged(isAltScreen);
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        /// <remarks>
        /// The grid read happens here, synchronously, rather than inside the async continuation.
        /// <c>TerminalView</c> sends the carriage return to the PTY before raising this, so the
        /// input line is already condemned: every scheduling hop between the keypress and the read
        /// widens the window in which the shell has begun repainting over it. This is as close to
        /// the keypress as the pane can get.
        /// </remarks>
        internal void OnCommandAssistEnterObserved()
        {
            // The reset is owed even when Command Assist is off or uninitialized: the accumulator
            // is fed from TermView events that fire regardless, and a line left in it would be
            // carried into the next one.
            if (!EnsureCommandAssistInitialized())
            {
                _marklessSubmission.Reset();
                return;
            }

            AssistQuerySnapshot? snapshot = _commandAssistController.TryReadQuerySnapshot();

            // Grid truth always wins where it exists, including when it says the line was empty:
            // "observed empty" and "unknown" are different answers, and only the second one falls
            // through to the accumulator. A poisoned accumulator answers null, which
            // CapturePipeline reads as "nothing to persist".
            string? submitted = snapshot is { } grid
                ? grid.Text
                : ReadEchoedMarklessSubmission();

            // After the read, before the await: Enter is both the capture point and the start of
            // the next line.
            _marklessSubmission.Reset();

            _ = HandleCommandAssistEnterObservedAsync(submitted);
        }

        /// <summary>
        /// The accumulator's answer, but only when the screen agrees with it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This gate exists to keep passwords out of history.</b> The accumulator is fed from
        /// <c>TerminalView.OnTextInput</c>, which fires for every keystroke unconditionally — it
        /// has no idea whether the shell is echoing. So in a markless session (`cmd.exe`, an
        /// un-instrumented SSH host), the sequence `ssh host` / Enter / `hunter2` / Enter leaves the
        /// accumulator holding a clean, unpoisoned `hunter2` at the hidden `password:` prompt, with
        /// no grid snapshot to override it — and it would be written to `history.jsonl` verbatim.
        /// <c>SecretsFilter</c> cannot save us there: it is pattern-based and a bare secret has no
        /// pattern.
        /// </para>
        /// <para>
        /// The check is the one thing that distinguishes the two cases. In any <em>visible</em>
        /// markless prompt the typed command is on the screen — only the <c>OSC 133;B</c> mark is
        /// missing, not the text — so requiring the accumulated string to be painted on the grid
        /// ending at the cursor costs a correct capture nothing. At a no-echo prompt the grid holds
        /// the prompt and nothing else, and the strings do not match.
        /// </para>
        /// <para>
        /// Conservative in every direction: no buffer, an alt screen, a cursor the reader will not
        /// resolve, an echo that has not landed yet, a shell that reprinted the line differently —
        /// all of them fall out as "no capture". Comparison is on text rather than columns, so a
        /// double-width character counts once (see
        /// <see cref="GridQueryReader.TryReadTextEndingAtCursor"/>).
        /// </para>
        /// </remarks>
        private string? ReadEchoedMarklessSubmission()
        {
            string? typed = _marklessSubmission.TryReadSubmission();
            if (string.IsNullOrEmpty(typed))
            {
                // Poisoned, or an empty line: either way there is nothing to prove.
                return typed;
            }

            TerminalBuffer? buffer = Buffer;
            if (buffer == null)
            {
                return null;
            }

            if (!GridQueryReader.TryReadTextEndingAtCursor(buffer, typed.Length, out string onScreen))
            {
                return null;
            }

            // Exactly typed.Length characters were requested, so equality is "the accumulated text
            // is painted on the grid and ends at the cursor". A short read (the row does not hold
            // that much) fails the same way a wrong read does.
            return string.Equals(onScreen, typed, StringComparison.Ordinal) ? typed : null;
        }

        /// <summary>
        /// The user typed printable text into the terminal. Feeds the markless accumulator and
        /// triggers a suggestion refresh; the text itself is content only for the accumulator.
        /// </summary>
        internal void NotifyTypedTextObserved(string text)
        {
            _marklessSubmission.AppendTypedText(text);
            NoteInputAwaitingEcho();
            if (EnsureCommandAssistInitialized())
            {
                _commandAssistController?.NotifyInputActivity();
            }
        }

        /// <summary>The user pressed Backspace; the accumulator drops its last character.</summary>
        internal void NotifyBackspaceObserved()
        {
            _marklessSubmission.ObserveBackspace();
            NoteInputAwaitingEcho();
            if (EnsureCommandAssistInitialized())
            {
                _commandAssistController?.NotifyInputActivity();
            }
        }

        /// <summary>
        /// Text arrived on the command line from somewhere other than the keyboard (a drag-and-drop
        /// or a clipboard paste).
        /// </summary>
        /// <remarks>
        /// Two mechanisms fire here and they are not the same one. The accumulator is
        /// <em>poisoned</em>, because it did not see the characters and can no longer describe the
        /// line. Command Assist is told the submission is <em>suppressed</em>, which is a
        /// provenance claim — the text on the line was not composed here — and that one applies to
        /// the grid path as well, where the line is perfectly readable and still should not be
        /// written to history as something the user typed. Either alone stops the paste being
        /// captured in a markless session; only suppression stops it in an instrumented one.
        /// </remarks>
        /// <param name="text">
        /// Ignored. The pasted text is deliberately not read: neither mechanism below wants it —
        /// the accumulator's answer to "what is on the line now" is "I cannot say", not "the line
        /// plus this", and suppression is a provenance flag with no payload. The parameter stays
        /// because it is the <c>TerminalView.PasteObserved</c> signature and a future consumer
        /// (length-based heuristics, say) would want it.
        /// </param>
        internal void NotifyPasteObserved(string text)
        {
            _ = text;
            _marklessSubmission.Poison();
            if (EnsureCommandAssistInitialized())
            {
                _commandAssistController?.NotifyPastedInput();
            }
        }

        /// <summary>
        /// Bytes reached this pane's PTY from somewhere other than its own keyboard handling: a
        /// broadcast from a sibling pane, the drag-and-drop path toast, a clipboard image path, the
        /// agent host's act surface. The accumulator can no longer describe the command line.
        /// </summary>
        /// <remarks>
        /// A poison rather than a reset, because none of these callers can say whether what they
        /// sent ended in a newline: unlike Enter, they leave the line in a state only the shell
        /// knows. Poison recovers at the next Enter, which costs at most one capture.
        /// </remarks>
        internal void NotifyExternalInputSent() => _marklessSubmission.Poison();

        private async Task HandleCommandAssistEnterObservedAsync(string? submittedText)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(submittedText))
                {
                    _lastRelevantCommandText = submittedText.Trim();
                }

                await _commandAssistController.HandleEnterAsync(submittedText);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Command Assist enter handling failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Records that this session emitted an OSC 133 mark, and - the first time only - republishes
        /// the session context so Command Assist learns the session is instrumented.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called from the four parser mark callbacks, i.e. on the PTY read thread. The context
        /// update itself has to happen on the UI thread (it reads <see cref="Session"/> and
        /// <c>CurrentWorkingDirectory</c> and pokes the controller), so it is posted rather than
        /// called.
        /// </para>
        /// <para>
        /// The posted update is not the only thing that closes the loop, and must not be: it lands
        /// asynchronously, so a shell whose <c>A</c>, <c>B</c> and <c>C</c> all arrive in one parse
        /// chunk could reach the capture pipeline before it. <c>AssistSessionContext</c> makes the
        /// same deduction independently from the event stream
        /// (<c>AssistSessionContext.IsShellIntegrationLive</c>). What the post is <em>necessary</em>
        /// for is durability: <c>UpdateSession</c> forgets observed markers whenever it is told
        /// integration is off, so without feeding the observation back, the next ordinary directory
        /// change would demote an instrumented remote back to markless.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Whether Nova participates in the OSC 133 contract at all for this pane: the same switch
        /// <see cref="ApplyShellIntegrationLaunchPlan"/> and
        /// <see cref="ArmRemoteShellIntegrationTracker"/> honour, read the same way (a pane with no
        /// settings object yet is treated as enabled, which is what the arming paths do).
        /// </summary>
        /// <remarks>
        /// Consulted by the two consumption paths that hang off the raw parser callbacks rather than
        /// off the tracker - the integrated-session latch and the <c>133;C</c> payload - because
        /// those callbacks are wired unconditionally and would otherwise keep consuming remote marks
        /// with the setting off. The callbacks themselves stay wired: they also feed the agent status
        /// machine and the overlay anchor, neither of which this switch governs.
        /// </remarks>
        private bool IsShellIntegrationConsumptionEnabled =>
            _settings?.CommandAssistShellIntegrationEnabled ?? true;

        private void NoteShellIntegrationMarkObserved()
        {
            if (_hasObservedShellIntegrationMark || !IsShellIntegrationConsumptionEnabled)
            {
                return;
            }

            _hasObservedShellIntegrationMark = true;
            Dispatcher.UIThread.Post(UpdateCommandAssistContext);
        }

        private void UpdateCommandAssistContext()
        {
            _commandAssistController?.UpdateSessionContext(
                shellKind: DetermineShellKind(Session?.ShellCommand ?? ShellCommand),
                workingDirectory: CurrentWorkingDirectory,
                profileId: Profile?.Id.ToString(),
                sessionId: Session?.Id.ToString(),
                hostId: Profile?.Type == ConnectionType.SSH ? Profile.SshHost : null,
                isRemote: Profile?.Type == ConnectionType.SSH,

                // Two ways to be integrated, and remote sessions can only ever be the second one.
                // "We injected a bootstrap" is unreachable over SSH; "the shell is emitting marks"
                // is what the V2 Phase 2b snippets buy, and it is equally good evidence - the parser
                // has never cared who installed the thing writing OSC 133.
                isShellIntegrated: _isShellIntegrationActive || _hasObservedShellIntegrationMark);
        }

        private void UpdatePaneContextMenuState()
        {
            UpdateRemoteFilesSidebarEntryPointState();

            if (RootGrid.ContextMenu?.Items is not IEnumerable<object> items)
            {
                return;
            }

            MenuItem? explainSelectionItem = items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MenuExplainSelection");
            if (explainSelectionItem != null)
            {
                bool canExplain = CanExplainSelection();
                explainSelectionItem.IsEnabled = canExplain;
                explainSelectionItem.IsVisible = canExplain;
            }
        }

        /// <summary>
        /// <c>TerminalView.KeyDownInterceptor</c>. Routes the key to Command Assist if it owns it,
        /// and observes every key either way for the markless submission accumulator.
        /// </summary>
        /// <remarks>
        /// The observation is strictly a side effect: this returns exactly what
        /// <see cref="TryRouteCommandAssistKey"/> returns, so input routing is unchanged. It has to
        /// run <em>after</em> the routing decision, because "Command Assist consumed this key" is
        /// the same fact as "the shell never saw it".
        /// </remarks>
        internal bool TryHandleCommandAssistKey(Key key, KeyModifiers modifiers)
        {
            bool handledByAssist = TryRouteCommandAssistKey(key, modifiers);
            _marklessSubmission.ApplyKey(key, modifiers, handledByAssist);
            return handledByAssist;
        }

        private bool TryRouteCommandAssistKey(Key key, KeyModifiers modifiers)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            CommandAssistController? controller = _commandAssistController;
            // Every fact here is asked of the controller rather than computed locally, including the two
            // that depend on this pane: IsAcceptOnEnterArmed folds in IsCommandAssistOverlayRendered
            // through the probe installed in InitializeCommandAssist, so the router, the hint strip and
            // this pane cannot hold three different opinions about who owns Enter.
            var keyState = new AssistKeyState(
                IsSurfaceVisible: controller?.ViewModel.IsVisible == true,
                IsAcceptOnEnterArmed: controller?.IsAcceptOnEnterArmed == true,
                IsSelectionUpOwned: controller?.IsSelectionUpOwned == true);
            if (!CommandAssistKeyRouter.IsAssistOwnedKey(
                    keyState,
                    AssistKeyMapper.ToAssistKey(key),
                    AssistKeyMapper.ToAssistModifiers(modifiers)))
            {
                return false;
            }

            bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
            bool isShift = (modifiers & KeyModifiers.Shift) != 0;
            bool isAlt = (modifiers & KeyModifiers.Alt) != 0;

            if (key == Key.Escape)
            {
                controller?.HandleEscape();
                return true;
            }

            // Accept-on-Enter (V2 Phase 3a). The router only says yes here while the popup is open with
            // a row selected *and the overlay is rendered*, so neither the typing flow nor a surface this
            // pane has hidden or dimmed reaches this branch.
            //
            // The return value is the insertion's, not `true`, and that is the important part: when
            // insertion refuses - a poisoned markless line, an unechoed keystroke, a cursor mid-line -
            // Enter falls through to the shell and submits, exactly as it did before this branch
            // existed. Consuming it instead would turn a refusal into a dead key, which is a strictly
            // worse answer than the pre-Phase-3a behavior the user is used to.
            if (key == Key.Enter && modifiers == KeyModifiers.None)
            {
                return TryInsertSelectedCommandAssistSuggestion();
            }

            if (key == Key.Down)
            {
                controller?.MoveSelectionDown();
                return true;
            }

            // Only reached when the router granted Up to the assist - an open popup, or a surface the user
            // summoned. In the passive states Up is never routed here at all, so the shell keeps its
            // history recall (PR #290 review).
            if (key == Key.Up)
            {
                controller?.MoveSelectionUp();
                return true;
            }

            if (isCtrl && !isShift && !isAlt && key == Key.Enter)
            {
                TryInsertSelectedCommandAssistSuggestion();
                return true;
            }

            if (isCtrl && isShift && key == Key.P)
            {
                if (controller == null || !controller.CanTogglePinSelection())
                {
                    return false;
                }

                _ = controller.TogglePinSelectionAsync();
                return true;
            }

            return false;
        }

        public bool TryToggleCommandAssistPinShortcut()
        {
            // Routes without observing: this arrives from the window's shortcut handler, which sees
            // the key before TerminalView does, so nothing was ever sent to the shell whether the
            // pin toggles or not.
            return TryRouteCommandAssistKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift);
        }

        /// <summary>
        /// The user pressed a key that edits the command line, and the PTY has been sent the bytes
        /// for it. Until the shell echoes them back and the parser paints them, the grid is a
        /// prefix of the real command line.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one desync the grid cannot self-report. Every other stale read looks wrong -
        /// a half-erased line, a mark that went dark - but an unechoed keystroke leaves a read that
        /// is internally perfect: <c>"git st"</c> with the cursor at offset 6, every planner guard
        /// satisfied, while the shell already holds <c>"git sta"</c>. Because the stale text is
        /// always a strict <em>prefix</em> of the true line, no prefix check can detect it: the
        /// planner would compute <c>"atus"</c> against <c>"git st"</c> and the line would become
        /// <c>"git staatus"</c>.
        /// </para>
        /// <para>
        /// So insertion refuses while this is set, consistent with the planner's other rules -
        /// refusal on doubt, never a guess. Ranking is deliberately left alone: a one-character-
        /// stale query ranks slightly worse rows and the next trigger fixes it, which is a cost
        /// worth paying to keep suggestions live while typing.
        /// </para>
        /// <para>
        /// The clear is approximate in one direction only. Any session output clears the flag, so
        /// unrelated output (a background job printing) can clear it before the echo lands, leaving
        /// the original window open. It cannot go the other way: output that has been parsed is
        /// output that is in the grid.
        /// </para>
        /// </remarks>
        internal void NoteInputAwaitingEcho() => _hasUnechoedInput = true;

        /// <summary>Session bytes have been parsed into the grid, so the grid has caught up.</summary>
        internal void NoteSessionOutputApplied() => _hasUnechoedInput = false;

        /// <summary>Whether an edit keystroke is still waiting for the shell's echo.</summary>
        internal bool HasUnechoedInput => _hasUnechoedInput;

        /// <summary>
        /// A popup row was clicked once: select it, exactly as <c>Up</c>/<c>Down</c> would.
        /// </summary>
        /// <remarks>
        /// Selecting rather than accepting is deliberate. A single click that ran an insertion would
        /// make the list unbrowsable by mouse — there would be no way to look at a row's detail panel
        /// without committing to it — and it would put a destructive action one stray click away on a
        /// surface that overlays the terminal. Accept needs a second, deliberate act: a double click, or
        /// a click on the row that is already selected.
        /// </remarks>
        internal void OnCommandAssistSuggestionPointerSelected(int index)
        {
            _commandAssistController?.TrySelectSuggestionAt(index);
        }

        /// <summary>A popup row was double-clicked, or clicked while already selected: accept it.</summary>
        internal void OnCommandAssistSuggestionPointerAccepted(int index)
        {
            CommandAssistController? controller = _commandAssistController;
            if (controller == null)
            {
                return;
            }

            // Select first even on the accept path: a double click on an unselected row must insert the
            // row that was clicked, not whatever the keyboard had highlighted.
            if (!controller.TrySelectSuggestionAt(index))
            {
                return;
            }

            TryInsertSelectedCommandAssistSuggestion();
        }

        private bool TryInsertSelectedCommandAssistSuggestion()
        {
            if (_commandAssistController == null || Session == null)
            {
                return false;
            }

            // The echo race: a fresh read taken now can be a self-consistent snapshot of a command
            // line the shell has already moved past. See NoteInputAwaitingEcho.
            if (_hasUnechoedInput)
            {
                return false;
            }

            // Read the line before accepting: accepting dismisses the surface, and the planner needs
            // to know what is on the command line *now* rather than what the last ranking pass saw.
            AssistQuerySnapshot? existingQuery = TryReadInsertionQuerySnapshot();

            // Everything above and below this line is non-mutating, and that ordering is the point.
            // TryAcceptSelection accepts *and* dismisses; calling it first meant that every refusal
            // after it - a degraded session, a cursor mid-line, a multiline entry - tore the list
            // down and sent nothing, so Ctrl+Enter read as "the feature is broken" rather than as
            // "not here". The plan is computed first and the surface is only touched once there is
            // text to send.
            if (!_commandAssistController.TryGetInsertionText(out string? insertionText) ||
                string.IsNullOrWhiteSpace(insertionText))
            {
                return false;
            }

            if (!CommandAssistInsertionPlanner.TryCreateInsertion(existingQuery, insertionText, out string? textToSend) ||
                string.IsNullOrEmpty(textToSend))
            {
                return false;
            }

            if (!_commandAssistController.TryAcceptSelection(out _))
            {
                return false;
            }

            _lastRelevantCommandText = insertionText;

            // Text on the command line the user did not type. Insertion is only reachable when the
            // grid can be read, so the accumulator is not the capture source here anyway - but it
            // is still holding a line it can no longer describe, and Ctrl+Enter is the one key the
            // interceptor deliberately does not poison on (Command Assist owns it).
            _marklessSubmission.Poison();
            Session.SendInput(textToSend);
            return true;
        }

        /// <summary>
        /// The command line the insertion planner is measured against: grid truth where it exists, and
        /// in a markless session an <em>observed-empty</em> line when - and only when - the accumulator
        /// can prove the line is empty.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>V2 Phase 3a: the "browse-only in degraded sessions" rule is narrowed, not dropped.</strong>
        /// Phase 1c refused all insertion without a snapshot, for a good reason: appending a whole
        /// command to an unknown prefix is how <c>git sgit status</c> happens. But "unknown" was doing
        /// two jobs. In the case the owner actually hit - open a markless SSH pane, press
        /// <c>Ctrl+R</c>, pick a row - the prefix is not unknown at all. It is empty, and the pane can
        /// prove it: the accumulator was reset by the last <c>Enter</c> (or <c>Ctrl+C</c>) and has
        /// observed nothing since, and it poisons on every edit it cannot model. So this returns the
        /// snapshot that says "the line was read and it is empty", which the planner already handles
        /// as a fact rather than an absence, and the whole command is sent.
        /// </para>
        /// <para>
        /// <strong>Why this is safe.</strong> Four independent things all have to agree, and each of
        /// them fails closed:
        /// </para>
        /// <list type="number">
        /// <item>the accumulator is <em>not poisoned</em> - so no arrow key, <c>Home</c>, <c>Delete</c>,
        /// <c>Tab</c>, paste, prior insertion, agent injection or unrecognised chord has touched the
        /// line since it was reset (the classification is an allow-list, so an unknown key poisons);</item>
        /// <item>the accumulator is <em>empty</em> - the user has typed nothing since the reset, so
        /// there is no prefix for the appended text to corrupt;</item>
        /// <item><see cref="_hasUnechoedInput"/> is clear, checked by the caller before this runs - so
        /// there are no keystrokes in flight to the shell that the pane has not seen come back. This is
        /// the condition that closes the "typed a character and hit Enter in the same frame" window,
        /// and it is the same gate the echo-race fix uses;</item>
        /// <item>the controller's own gates still apply - a suppressed (pasted) submission and an alt
        /// screen both refuse upstream of here.</item>
        /// </list>
        /// <para>
        /// If any of them is in doubt the answer is <see langword="null"/> and the planner refuses,
        /// exactly as before. And the failure mode of the remaining risk is bounded in a way the
        /// original refusal's was not: insertion sends text to the shell's line editor and stops. The
        /// user sees the command sitting on their prompt and has to press <c>Enter</c> themselves, so
        /// the worst case is a visible, editable, deletable line - not a command that ran.
        /// </para>
        /// <para>
        /// One honest cost: "the accumulator is clean and empty" is not the same as "the shell is at a
        /// prompt". A markless pane running a program that reads stdin (<c>cat</c>, a REPL) satisfies
        /// every condition, so an accepted row is typed into that program instead. That is what typing
        /// the command by hand would also have done, and it is visible either way; the alternative -
        /// refusing forever in every un-instrumented session - is the bug being fixed.
        /// </para>
        /// </remarks>
        private AssistQuerySnapshot? TryReadInsertionQuerySnapshot()
        {
            AssistQuerySnapshot? gridTruth = _commandAssistController?.TryReadQuerySnapshot();
            if (gridTruth.HasValue)
            {
                return gridTruth;
            }

            if (!_marklessSubmission.IsCleanAndEmpty)
            {
                return null;
            }

            // Deliberately constructed rather than read: the cursor is at offset 0 of an empty line,
            // the entry is not multiline and no right prompt was trimmed, because none of those things
            // can be true of a line with no characters in it. Going through the planner rather than
            // sending the text directly keeps one refusal discipline for both paths.
            return new AssistQuerySnapshot(
                Text: string.Empty,
                CursorOffset: 0,
                IsMultiline: false,
                RightPromptTrimmed: false);
        }

        internal static string DetermineShellKind(string? shellCommand)
        {
            if (string.IsNullOrWhiteSpace(shellCommand))
            {
                return "unknown";
            }

            // Order matters: bash/zsh/fish must be matched before the generic
            // `sh` fallback because each contains "sh" as a substring.
            if (shellCommand.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                shellCommand.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "pwsh";
            }

            if (shellCommand.Contains("cmd", StringComparison.OrdinalIgnoreCase))
            {
                return "cmd";
            }

            if (shellCommand.Contains("bash", StringComparison.OrdinalIgnoreCase))
            {
                return "bash";
            }

            if (shellCommand.Contains("zsh", StringComparison.OrdinalIgnoreCase))
            {
                return "zsh";
            }

            if (shellCommand.Contains("fish", StringComparison.OrdinalIgnoreCase))
            {
                return "fish";
            }

            if (shellCommand.Contains("sh", StringComparison.OrdinalIgnoreCase))
            {
                return "sh";
            }

            return "unknown";
        }

        private void InitializeSession(string? shell, TerminalProfile? profile, int cols, int rows, string? explicitArgs = null)
        {
            if (Session != null || Buffer == null) return;

            if (cols <= 0 || rows <= 0) return;

            // Update buffer to match view exactly before starting PTY
            Buffer.Resize(cols, rows);
            CreateAndWireParser();

            // Sync initial metrics
            float cw = TermView.Metrics.CellWidth;
            float ch = TermView.Metrics.CellHeight;
            if (cw > 0) Parser!.CellWidth = cw;
            if (ch > 0) Parser!.CellHeight = ch;

            // Setup Session
            string effectiveShell = shell ?? ShellHelper.GetDefaultShell();
            string args = explicitArgs ?? profile?.Arguments ?? "";
            InitializeSessionCore(effectiveShell, args, profile, cols, rows);
        }

        /// <summary>
        /// Replaces <see cref="Parser"/> with a fresh <see cref="AnsiParser"/> and attaches this
        /// pane's handlers to it.
        /// </summary>
        /// <remarks>
        /// The handlers attached here are deliberately never removed, and that is safe for exactly
        /// one reason: a <b>new</b> parser is created on every call, so each one starts with an empty
        /// handler list and the previous parser becomes garbage along with its handlers. #102 read
        /// the missing <c>-=</c> calls as an accumulation bug on <c>Reconnect()</c>; they are not,
        /// because of this line.
        ///
        /// That makes the assignment load-bearing. Hoisting the parser out to reuse it across
        /// sessions — a reasonable-looking change — would silently double all of these on every
        /// reconnect, producing the duplicate bell/title symptom #102 describes, with no <c>-=</c>
        /// anywhere to fall back on. Split into its own method so that invariant can be asserted
        /// without spinning up a shell; see <c>PaneParserWiringTests</c>.
        /// </remarks>
        internal void CreateAndWireParser()
        {
            if (Buffer == null) return;

            Parser = new AnsiParser(Buffer);

            // A device reply is text on the PTY that the keyboard path never produced: DA1, a DSR
            // cursor report, an answerback. Nothing here can promise the shell's line editor was
            // not reading when the query arrived, and a reply that lands in a line editor is
            // literal input in exactly the way a paste is - so the accumulator can no longer
            // describe the line. Poison rather than reset, for the same reason
            // NotifyExternalInputSent poisons: the writer cannot say whether what it sent ended the
            // line. Subscribed here rather than beside the SendInput handler in
            // InitializeSessionCore so it exists whenever a parser does, session or not.
            Parser.OnResponse += _ => _marklessSubmission.Poison();

            // OSC 10/11 (fg/bg color query) answers come from the active theme; without this,
            // a freshly-created parser would fall back to AnsiParser's hardcoded defaults until
            // the next ApplySettings call (#265). Must use the same profile-merged theme
            // resolution as ApplySettings (via BuildEffectiveSettings) — using the global
            // _settings.ActiveTheme directly would clobber a per-profile theme override,
            // since this method runs after ApplySettings on pane init and on every
            // Reconnect().
            if (_settings != null)
            {
                var effectiveTheme = BuildEffectiveSettings(_settings).ActiveTheme;
                Parser.DefaultForeground = effectiveTheme.Foreground;
                Parser.DefaultBackground = effectiveTheme.Background;
            }

            // Kill switch (Blocker 2, #277 review): a freshly-created parser must start with
            // the query-reply gate in the same state TerminalView's encoder gate will be in,
            // same reasoning as the DefaultForeground wiring above.
            Parser.KittyKeyboardEnabled = _settings?.EnableKittyKeyboardProtocol ?? true;

            Parser.OnBell += () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    TermView.TriggerBell();
                    BellReceived?.Invoke(this);
                });
            };
            // OSC 52 clipboard write (issue #268). AnsiParser always raises this event
            // (VT stays policy-free); the settings gate lives here, checked at invocation
            // time so a live ApplySettings toggle takes effect immediately without needing
            // to re-wire the parser. Read (query) is handled entirely inside the parser via
            // an OnResponse denial reply and never reaches this handler.
            Parser.OnClipboardWrite += (target, data) =>
            {
                // Resolved through BuildEffectiveSettings rather than off raw _settings, matching
                // the #277 precedent below in ApplySettings: BuildEffectiveSettings copies this
                // bool straight through today, so the two are equivalent, but the moment a
                // per-profile / SSH-scoped override lands, reading the global would silently gate
                // on the wrong value (PR #280 review). Still read at invocation time so a live
                // ApplySettings toggle takes effect without re-wiring.
                // A null _settings means "not configured yet" and defaults to allow, consistent
                // with TerminalSettings.AllowOsc52ClipboardWrite's own default.
                bool allowed = _settings == null || BuildEffectiveSettings(_settings).AllowOsc52ClipboardWrite;
                if (!allowed) return;

                // Lossy by design: invalid UTF-8 in the payload becomes U+FFFD rather than being
                // rejected, matching how other terminals treat OSC 52 as text. GetString does not
                // throw for malformed input, so there is nothing to catch here - if this should
                // ever reject non-UTF-8 instead, it needs a throwing decoder, not a try/catch.
                string text = System.Text.Encoding.UTF8.GetString(data);

                // Bumped only once the gate above has passed, i.e. exactly when this handler is
                // actually about to reach the clipboard. Gives PaneParserWiringTests a synchronous
                // seam to assert "setting off -> clipboard not touched" without needing a real
                // UI-thread TopLevel/Clipboard in tests. Deliberately a plain non-atomic increment
                // on the PTY thread: it is a single-writer test seam, not a metric, so don't read
                // it as one.
                _clipboardWriteAttemptsForTest++;
                Dispatcher.UIThread.Post(() =>
                {
                    _ = TermView.SetClipboardTextAsync(text);
                });
            };
            Parser.OnWorkingDirectoryChanged += cwd =>
            {
                _shellLifecycleTracker?.HandleWorkingDirectoryChanged(cwd);
                Dispatcher.UIThread.Post(() =>
                {
                    HandleWorkingDirectoryChanged(cwd);
                });
            };
            Parser.OnTitleChanged += title =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CurrentOscTitle = title;
                    TitleChanged?.Invoke(this, title);
                });
            };
            Parser.OnPromptReady += () =>
            {
                NoteShellIntegrationMarkObserved();
                _shellLifecycleTracker?.HandlePromptReady();
                _agentRegistration?.StatusMachine.NotifyPromptReady();
            };
            Parser.OnCommandAccepted += commandText =>
            {
                NoteShellIntegrationMarkObserved();

                // Only overwritten when the mark carried text. A bare `133;C` (legal FinalTerm, and
                // what several third-party remote snippets emit) arrives with null, and clearing
                // here would throw away the command the grid/heuristic path already read at Enter -
                // which on those shells is the only source Fix mode has.
                //
                // Gated on the setting for the same reason the latch above is: with shell
                // integration off, a C payload written by the far end of an SSH connection (or by a
                // local `cat` of a crafted file) must not become the command Fix mode and the
                // long-command notification talk about.
                if (!string.IsNullOrWhiteSpace(commandText) && IsShellIntegrationConsumptionEnabled)
                {
                    _lastRelevantCommandText = commandText.Trim();
                }

                _shellLifecycleTracker?.HandleCommandAccepted(commandText);
                // OSC 133;C is the execution-start edge: the line editor is closed and the
                // shell is about to run the command. (OSC 133;B, below, only says the prompt
                // finished printing -- the shell is idle waiting for input at that point, so
                // driving "running" off B would report every idle prompt as a busy session.)
                //
                // Status machine is notified synchronously on the parser path so
                // command-lifecycle signals keep their emission order relative to
                // OnCommandStarted/OnPromptReady (the UI post below would let a
                // snapshot briefly see AwaitingInput with CurrentCommand set).
                _agentRegistration?.StatusMachine.NotifyCommandAccepted(commandText);
                _agentRegistration?.StatusMachine.NotifyCommandStarted();
                _lastCommandStartedAtUtc = DateTimeOffset.UtcNow;
                Dispatcher.UIThread.Post(() =>
                {
                    LastExitCode = null;
                    CommandStarted?.Invoke(this);
                });
            };
            Parser.OnCommandStarted += mark =>
            {
                // OSC 133;B == prompt end / start of user input. The mark position is the
                // anchor Command Assist uses to read the live command line out of the grid.
                // B rides inside the prompt string, so it is re-emitted on every repaint
                // (resize, clear, zle reset-prompt) with fresh coordinates: keep the newest
                // one rather than the first.
                NoteShellIntegrationMarkObserved();
                lock (_commandStartMarkGate)
                {
                    _latestCommandStartMark = mark;
                }

                _shellLifecycleTracker?.HandleCommandStarted(new ShellMarkPosition(
                    Row: mark.Row,
                    Column: mark.Column,
                    AbsoluteRow: mark.AbsoluteRow,
                    IsAltScreen: mark.IsAltScreen,
                    Generation: mark.Generation));
            };
            Parser.OnCommandFinished += exitCode =>
            {
                _agentRegistration?.StatusMachine.NotifyCommandFinished(exitCode);
                Dispatcher.UIThread.Post(() =>
                {
                    if (exitCode.HasValue)
                    {
                        LastExitCode = exitCode.Value;
                    }

                    CommandFinished?.Invoke(this, exitCode);
                    _ = HandleCommandAssistCompletionAsync(exitCode);
                });
            };
            Parser.OnCommandFinishedDetailed += (exitCode, durationMs) =>
            {
                // OSC 133;D == the command finished, so the B mark that anchored its input line
                // no longer points at an input line: everything from it down is command output.
                // Dropping it here closes the window in which the grid reader would happily
                // return output as "the live command line" -- until the next prompt re-emits B,
                // there is nothing truthful to read, and "no mark" is the honest answer.
                //
                // D rather than C (CommandExecuted) deliberately: in one B -> C -> D cycle, C
                // fires the instant the user submits, while the input line is still on screen and
                // still exactly what the mark describes. Clearing on C would blind the reader for
                // the whole run of the command, including the submission edge that Phase 1c reads
                // the final command text on. GridQueryReader.MaxSpanRows stays as a backstop for
                // shells that emit B without a matching D, but it is no longer the only guard.
                NoteShellIntegrationMarkObserved();
                lock (_commandStartMarkGate)
                {
                    _latestCommandStartMark = null;
                }

                _shellLifecycleTracker?.HandleCommandFinished(exitCode, durationMs);

                // Long-command completion (A2 PR4): the pane only applies the
                // mechanical threshold; opt-in + focus policy lives in the window.
                var startedAt = _lastCommandStartedAtUtc;
                _lastCommandStartedAtUtc = null;
                TimeSpan? duration = durationMs.HasValue
                    ? TimeSpan.FromMilliseconds(durationMs.Value)
                    : startedAt.HasValue ? DateTimeOffset.UtcNow - startedAt.Value : null;
                if (duration is { } d && LongCommandNotificationPolicy.QualifiesAsLong(d))
                {
                    var commandText = _lastRelevantCommandText;
                    Dispatcher.UIThread.Post(() => LongCommandCompleted?.Invoke(this, commandText, exitCode, d));
                }
            };
        }

        /// Spawns the session and wires the handlers that depend on it. Split out of
        /// <c>InitializeSession</c> alongside <see cref="CreateAndWireParser"/>; no behaviour change.
        private void InitializeSessionCore(string effectiveShell, string args, TerminalProfile? profile, int cols, int rows)
        {
            _shellLifecycleTracker = null;
            _isShellIntegrationActive = false;
            _hasObservedShellIntegrationMark = false;
            _shellIntegrationEnvOverrides = null;
            // A restart or a profile switch reaches here; the pending line belonged to the shell
            // that is going away.
            _marklessSubmission.Reset();
            lock (_commandStartMarkGate)
            {
                _latestCommandStartMark = null;
            }

            // Update SFTP Menu Visibility
            // If it's not an SSH session, detach the context menu entirely to avoid "tiny empty box" artifacts
            if (profile == null || profile.Type != ConnectionType.SSH)
            {
                RootGrid.ContextMenu = null;
            }

            UpdateRemoteFilesSidebarEntryPointState();

            string startingDir = profile?.StartingDirectory ?? "";
            Session = null;
            _agentRegistration?.SetLifecycle(null);
            try
            {
                // If effectiveShell contains a space and is not a direct file, it's likely a combined command.
                if (effectiveShell.Contains(' ') && !System.IO.File.Exists(effectiveShell))
                {
                    int firstSpace = effectiveShell.IndexOf(' ');
                    string cmdPart = effectiveShell.Substring(0, firstSpace);
                    string argPart = effectiveShell.Substring(firstSpace + 1);

                    effectiveShell = cmdPart;
                    args = (argPart + " " + args).Trim();
                }

                ShellCommand = effectiveShell;
                ShellArgs = args;

                if (profile == null || profile.Type != ConnectionType.SSH)
                {
                    ApplyShellIntegrationLaunchPlan(profile, ref effectiveShell, ref args, startingDir);
                    ShellCommand = effectiveShell;
                    ShellArgs = args;
                }
                else
                {
                    ArmRemoteShellIntegrationTracker(profile);
                }

                if (profile != null && profile.Type == ConnectionType.SSH)
                {
                    try
                    {
                        var sessionFactory = new SshSessionFactory(
                            nativeInteractionHandler: SshInteractionHandler,
                            nativeSshEnabled: _settings?.ExperimentalNativeSshEnabled ?? false);
                        Session = sessionFactory.Create(
                            profile.Id,
                            cols,
                            rows,
                            _sshDiagnosticsLevel,
                            null,
                            log: TerminalLogger.Log);
                        ShellCommand = Session.ShellCommand;
                        ShellArgs = string.Empty;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TerminalPane] SSH connection failed for '{profile.Name}': {ex.Message}");
                        WriteBanner($"\r\n[ERROR] SSH Connection Failed: {SanitizeBannerValue(ex.Message)}\r\n");

                        // Fail loudly: Do not fall back to RustPtySession with missing arguments.
                        return;
                    }
                }

                Session ??= new RustPtySession(
                    effectiveShell,
                    cols,
                    rows,
                    args,
                    startingDir,
                    skipPowerShellPostLaunchInit: _isShellIntegrationActive,
                    environmentOverrides: _shellIntegrationEnvOverrides);

                TermView.SetSession(Session);
                ITerminalSession session = Session;
                session.OnExit += code =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        HandleSessionExit(session, code);
                    });
                };
                RegisterActiveSshSession(session, profile);
                UpdateCommandAssistContext();

                // Publish the PTY lifecycle to the registration (the endpoint's
                // sweep probes only this published reference, never the pane).
                if (_agentRegistration is { } agentReg)
                {
                    agentReg.SetLifecycle(session);

                    // Seed the first child-process sample, but OFF the UI thread:
                    // ProbeHasActiveChildProcesses() is a full OS process-table scan
                    // (CreateToolhelp32Snapshot on Windows, a pgrep spawn elsewhere) —
                    // blocking I/O that would jank tab creation. It is thread-safe, so
                    // run the probe on a background thread and post only the Sweep back
                    // to the UI thread. The endpoint's 1 s sweep corrects it regardless.
                    Task.Run(() =>
                    {
                        var hasChildren = agentReg.ProbeHasActiveChildProcesses();
                        Dispatcher.UIThread.Post(
                            () => agentReg.StatusMachine.Sweep(hasChildren),
                            DispatcherPriority.Background);
                    });
                }
            }
            catch (Exception ex)
            {
                // Graceful failure: Log and show in terminal
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Failed to spawn session: {ex.Message}");
                WriteBanner($"\r\n[ERROR] Failed to spawn process: {SanitizeBannerValue(effectiveShell)}\r\n[DETAILS] {SanitizeBannerValue(ex.Message)}\r\n");
                return;
            }

            // Wire up Output
            Session.OnOutputReceived += text =>
            {
                Parser.Process(text);

                // After Parse, not before: the flag's contract is "the grid may be behind the
                // keyboard", so it may only be cleared once these bytes are actually painted.
                NoteSessionOutputApplied();
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateScrollUI();
                    OutputReceived?.Invoke(this);
                });
            };

            // Wire up Parser responses (e.g. DA1). The accumulator invalidation for these lives in
            // CreateAndWireParser, next to the parser's other observers.
            Parser.OnResponse += response =>
            {
                Session.SendInput(response);
            };

            WireReusedTermViewHandlers();
        }

        /// <summary>
        /// Subscribes the two <see cref="TermView"/> handlers that belong to session setup, in a way
        /// that is safe to call repeatedly.
        /// </summary>
        /// <remarks>
        /// Every other subscription made by <c>InitializeSession</c> targets an object that is
        /// recreated with the session — a fresh <see cref="AnsiParser"/> (see the assignment at the
        /// top of that method) or the new <see cref="ITerminalSession"/> — so those handler lists
        /// start empty and cannot accumulate. These two are the exception: <c>TermView</c> is the
        /// pane's own control and outlives any individual session, so <c>Reconnect()</c> would add a
        /// second copy of each on every reconnect.
        ///
        /// Hence the cached delegates plus remove-before-add: the delegate identity has to be stable
        /// for <c>-=</c> to match, which a fresh lambda per call would not give. Extracted into its
        /// own method so the idempotence can be asserted directly (#102) rather than only through a
        /// full session spin-up.
        /// </remarks>
        internal void WireReusedTermViewHandlers()
        {
            _onTermViewResize ??= (c, r) =>
            {
                if (Parser != null)
                {
                    float cwResize = TermView.Metrics.CellWidth;
                    float chResize = TermView.Metrics.CellHeight;
                    if (cwResize > 0) Parser.CellWidth = cwResize;
                    if (chResize > 0) Parser.CellHeight = chResize;
                }
                Session?.Resize(c, r);
            };
            TermView.OnResize -= _onTermViewResize;
            TermView.OnResize += _onTermViewResize;

            _onTermViewMetricsChanged ??= (cwMetric, chMetric) =>
            {
                if (Parser != null && cwMetric > 0 && chMetric > 0)
                {
                    Parser.CellWidth = cwMetric;
                    Parser.CellHeight = chMetric;
                }

                // Cell geometry just changed, so the agent-host's copy of this
                // pane's render inputs is stale (A5 captureScreen).
                UpdateAgentRenderParameters();
            };
            TermView.MetricsChanged -= _onTermViewMetricsChanged;
            TermView.MetricsChanged += _onTermViewMetricsChanged;

            // The view may already have measured its font before this subscription
            // existed, in which case the MetricsChanged that would have published
            // the render parameters has already been and gone.
            UpdateAgentRenderParameters();
        }

        private void HandleWorkingDirectoryChanged(string cwd)
        {
            CurrentWorkingDirectory = cwd;
            UpdateCommandAssistContext();
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            WorkingDirectoryChanged?.Invoke(this, cwd);
        }



        /// <summary>
        /// Merges global settings with this pane's profile overrides (font, theme, cursor).
        /// Shared by <see cref="ApplySettings"/> (which needs every merged field for
        /// <see cref="TermView"/>) and <see cref="CreateAndWireParser"/> (which only needs
        /// the resulting <see cref="TerminalSettings.ActiveTheme"/> colors for OSC 10/11
        /// answers), so the two call sites can never resolve the profile-effective theme
        /// differently — see the #265 wiring-bug follow-up.
        /// </summary>
        private TerminalSettings BuildEffectiveSettings(TerminalSettings settings)
        {
            // We create a "copy" for the view to use, but we only override specific visual fields
            return new TerminalSettings
            {
                FontSize = Profile?.FontSize ?? settings.FontSize,
                FontFamily = Profile?.FontFamily ?? settings.FontFamily,
                ThemeName = Profile?.ThemeName ?? settings.ThemeName,
                CursorStyle = Profile?.CursorStyle ?? settings.CursorStyle,
                CursorBlink = Profile?.CursorBlink ?? settings.CursorBlink,

                // Inherit everything else from global
                MaxHistory = settings.MaxHistory,
                WindowOpacity = settings.WindowOpacity,
                BlurEffect = settings.BlurEffect,
                BackgroundImagePath = settings.BackgroundImagePath,
                BackgroundImageOpacity = settings.BackgroundImageOpacity,
                BackgroundImageStretch = settings.BackgroundImageStretch,
                BellAudioEnabled = settings.BellAudioEnabled,
                BellVisualEnabled = settings.BellVisualEnabled,
                SmoothScrolling = settings.SmoothScrolling,
                EnableLinkDetection = settings.EnableLinkDetection,
                EnableKittyKeyboardProtocol = settings.EnableKittyKeyboardProtocol,
                AllowOsc52ClipboardWrite = settings.AllowOsc52ClipboardWrite,
                CommandAssistEnabled = settings.CommandAssistEnabled,
                CommandAssistHistoryEnabled = settings.CommandAssistHistoryEnabled,
                CommandAssistMaxHistoryEntries = settings.CommandAssistMaxHistoryEntries,
                CommandAssistAutoHideInAltScreen = settings.CommandAssistAutoHideInAltScreen,
                CommandAssistShellIntegrationEnabled = settings.CommandAssistShellIntegrationEnabled,
                CommandAssistPowerShellIntegrationEnabled = settings.CommandAssistPowerShellIntegrationEnabled,
                Profiles = settings.Profiles,
                DefaultProfileId = settings.DefaultProfileId
            };
        }

        public void ApplySettings(TerminalSettings settings)
        {
            _settings = settings;

            // Merge global settings with profile overrides
            var effectiveSettings = BuildEffectiveSettings(settings);

            TermView.ApplySettings(effectiveSettings);
            if (_commandAssistController != null || !IsCommandAssistFeatureEnabled())
            {
                InitializeCommandAssist();
            }
            UpdateMinimumSizeConstraints();

            // Sync metrics to parser after settings change (font size, etc.)
            if (Parser != null)
            {
                float cw = TermView.Metrics.CellWidth;
                float ch = TermView.Metrics.CellHeight;
                if (cw > 0) Parser.CellWidth = cw;
                if (ch > 0) Parser.CellHeight = ch;

                // Keep OSC 10/11 (fg/bg color query) responses in sync with the active theme,
                // including profile-specific theme overrides (see effectiveSettings above).
                Parser.DefaultForeground = effectiveSettings.ActiveTheme.Foreground;
                Parser.DefaultBackground = effectiveSettings.ActiveTheme.Background;

                // Kill switch (Blocker 2, #277 review): keep the query-reply gate in sync with
                // the setting on every settings change, not just at parser creation.
                Parser.KittyKeyboardEnabled = effectiveSettings.EnableKittyKeyboardProtocol;
            }

            // Font family/size and shaping toggles just moved with the settings.
            UpdateAgentRenderParameters();
        }


        private void ScrollBar_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingScroll || Buffer == null) return;

            // ScrollBar Top (0) -> History Top (Max Offset)
            // ScrollBar Bottom (Max) -> History Bottom (0 Offset)
            int inverted = (int)(TermScrollBar.Maximum - e.NewValue);
            TermView.ScrollOffset = inverted;
            TermView.InvalidateVisual();
        }

        private void UpdateScrollUI()
        {
            if (Buffer == null) return;

            _isUpdatingScroll = true;
            try
            {
                int total = Buffer.TotalLines;
                int view = Buffer.Rows;
                int maxScroll = Math.Max(0, total - view);

                TermScrollBar.Maximum = maxScroll;
                TermScrollBar.ViewportSize = view;

                // Current Value
                // Offset 0 (Bottom) -> Value = Max
                // Offset Max (Top) -> Value = 0
                TermScrollBar.Value = maxScroll - TermView.ScrollOffset;
            }
            finally
            {
                _isUpdatingScroll = false;
            }

            // When new output arrives, ensure the cursor is visible
            // If we just switched from alt screen (like after exiting mc), ensure we're scrolled to show the cursor
            Dispatcher.UIThread.Post(async () =>
            {

                if (TermView.JustSwitchedFromAltScreen)
                {
                    // Small delay to ensure screen switch processing is complete
                    await Task.Delay(10);
                    // Ensure cursor is visible after screen switch
                    TermView.EnsureCursorVisible();
                    // Note: EnsureCursorVisible() handles resetting the flag internally
                }
                else
                {
                    // For normal output, ensure the cursor is visible
                    TermView.EnsureCursorVisible();
                }

            }, DispatcherPriority.Render);

            // Failsafe: Force render on output
            TermView.InvalidateVisual();
        }

        private void SetupSearch()
        {
            void OnSearchTriggered(object? s, global::Avalonia.Interactivity.RoutedEventArgs e) => PerformSearch();

            SearchBox.TextChanged += (s, e) => PerformSearch();

            // Re-run search when options change
            SearchCaseSensitive.Click += OnSearchTriggered;
            SearchRegex.Click += OnSearchTriggered;

            SearchPrev.Click += (s, e) => TermView.PrevMatch();
            SearchNext.Click += (s, e) => TermView.NextMatch();
            SearchClose.Click += (s, e) =>
            {
                SearchPanel.IsVisible = false;
                TermView.ClearSearch();
                TermView.Focus();
            };

            TermView.SearchStateChanged += (idx, total) =>
            {
                Dispatcher.UIThread.Post(() => SearchCount.Text = $"{idx}/{total}");
            };
        }

        private void PerformSearch()
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                bool useRegex = SearchRegex.IsChecked ?? false;
                bool caseSensitive = SearchCaseSensitive.IsChecked ?? false;
                TermView.Search(SearchBox.Text, useRegex, caseSensitive);
            }
            else
            {
                TermView.ClearSearch();
            }
        }

        public void ToggleSearch()
        {
            SearchPanel.IsVisible = !SearchPanel.IsVisible;
            if (SearchPanel.IsVisible)
            {
                SearchBox.Focus();
                PerformSearch();
            }
            else
            {
                TermView.ClearSearch();
                TermView.Focus();
            }
        }

        public void ToggleCommandAssist()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            _commandAssistController?.ToggleAssist();
        }

        public bool OpenCommandAssistHelp()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return false;
            }

            return _commandAssistController?.OpenHelp() ?? false;
        }

        public bool OpenCommandAssistHistorySearch()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return false;
            }

            return _commandAssistController?.OpenHistorySearch() ?? false;
        }

        public void NotifyCommandAssistPaste(string text)
        {
            // Poisons before the feature gate: the accumulator is fed unconditionally, so it must
            // be invalidated unconditionally. See NotifyPasteObserved for how poison and
            // suppression divide the work.
            _marklessSubmission.Poison();

            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            // The text is kept for the failure-analysis path (Fix mode needs a command to analyse
            // and a paste is often the whole of one), but it is no longer handed to Command Assist
            // as query state: NotifyPastedInput carries provenance only. See
            // CommandAssistController.NotifyPastedInput.
            if (!string.IsNullOrWhiteSpace(text))
            {
                _lastRelevantCommandText = text.Trim();
            }

            _commandAssistController?.NotifyPastedInput();
        }

        internal bool CanExplainSelection(string? selectedTextOverride = null)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            string? selectedText = selectedTextOverride ?? TermView.GetSelectedText();
            return !string.IsNullOrWhiteSpace(selectedText);
        }

        internal async Task<bool> ExplainSelectionAsync(string? selectedTextOverride = null)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return false;
            }

            string? selectedText = selectedTextOverride ?? TermView.GetSelectedText();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return false;
            }

            return await _commandAssistController.ExplainSelectionAsync(selectedText);
        }

        public void ToggleRenderHud()
        {
            TermView.ShowRenderHud = !TermView.ShowRenderHud;
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            UpdateFocusVisuals(true);
            TermView.InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            UpdateFocusVisuals(false);
        }

        private void UpdateFocusVisuals(bool focused)
        {
            if (InactiveOverlay != null)
            {
                bool dimEnabled = true;
                InactiveOverlay.IsVisible = dimEnabled && !IsActivePane;
            }

            if (FocusBorder != null)
            {
                FocusBorder.IsVisible = false;
            }

            // Keep rendering crisp; dimming is now handled by overlay.
            TermView.Opacity = 1.0;
            AutomationProperties.SetName(TermView, focused ? "Terminal Pane Active" : "Terminal Pane");

            // Re-render to ensure cursor state updates
            TermView.InvalidateVisual();
        }

        public (double MinWidth, double MinHeight) GetMinimumPaneSize()
        {
            UpdateMinimumSizeConstraints();
            return (MinWidth, MinHeight);
        }

        private void UpdateMinimumSizeConstraints()
        {
            float cellWidth = TermView.Metrics.CellWidth > 0 ? TermView.Metrics.CellWidth : 8f;
            float cellHeight = TermView.Metrics.CellHeight > 0 ? TermView.Metrics.CellHeight : 18f;

            // UX spec: minimum 20 cols x 5 rows.
            MinWidth = Math.Ceiling((cellWidth * 20) + 4);
            MinHeight = Math.Ceiling(cellHeight * 5);

            if (_settings != null && InactiveOverlay != null)
            {
                var bg = _settings.ActiveTheme.Background;
                double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                byte alpha = (byte)(luminance > 0.5 ? 96 : 72);
                InactiveOverlay.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Focus Handling: Ensure we are focused
            if (!IsKeyboardFocusWithin) return;

            var modifiers = e.KeyModifiers;
            bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
            bool isShift = (modifiers & KeyModifiers.Shift) != 0;

            // Search Shortcut (Ctrl+Shift+F)
            if (isCtrl && isShift && e.Key == Key.F)
            {
                ToggleSearch();
                e.Handled = true;
                return;
            }

            // Copy/Paste (Ctrl+Shift+C/V) - TBD
            // Font Zoom - TBD

            // Reconnect if dead
            if (ShouldReconnectOnEnter(Session) && e.Key == Key.Enter)
            {
                e.Handled = true;
                Reconnect();
                return;
            }

            // Forward to PTY common handler
            // For now, we rely on Window forwarding, OR we implement it here.
            // PLAN: We will implement full OnKeyDown here in Phase 2.

            base.OnKeyDown(e);
        }

        public void Reconnect()
        {
            CloseRemoteFilesSidebar();

            if (Session != null)
            {
                ITerminalSession session = Session;
                UnregisterActiveSshSession(session);
                Session = null;
                _agentRegistration?.SetLifecycle(null);
                session.Dispose();
            }

            LastExitCode = null;
            WriteBanner("\r\n\x1b[90m[Reconnecting...]\x1b[0m\r\n");
            InitializeSession(ShellCommand, Profile, TermView.Cols, TermView.Rows, ShellArgs);
        }

        internal static bool ShouldReconnectOnEnter(ITerminalSession? session)
        {
            return session == null || !session.IsProcessRunning;
        }

        internal void ConfigureRemoteFilesSidebarForTest(IRemoteDirectoryBrowserService directoryBrowserService)
        {
            _isRemoteFilesSidebarTestServiceConfigured = true;
            SetRemoteFilesSidebarService(directoryBrowserService);
            UpdateRemoteFilesSidebarCurrentDirectoryState();
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        internal void ShowRemoteFilesSidebarForTest()
        {
            if (!_isRemoteFilesSidebarTestServiceConfigured)
            {
                ConfigureRemoteFilesSidebarForTest(new TestRemoteDirectoryBrowserService());
            }

            if (!IsRemoteFilesSidebarSupported())
            {
                Profile = new TerminalProfile
                {
                    Name = "Test Native SSH",
                    Type = ConnectionType.SSH,
                    SshBackendKind = SshBackendKind.Native,
                    SshHost = "test.example",
                    SshUser = "nova"
                };
            }

            OpenRemoteFilesSidebarAsync(Profile?.Id ?? Guid.NewGuid(), Session?.Id ?? Guid.NewGuid())
                .GetAwaiter()
                .GetResult();
        }

        internal void HandleAltScreenChangedForTest(bool isAltScreen)
        {
            if (Buffer != null)
            {
                FieldInfo? field = typeof(TerminalBuffer).GetField("_isAltScreen", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(Buffer, isAltScreen);
            }

            HandleAltScreenChanged(isAltScreen);
        }

        internal void HandleWorkingDirectoryChangedForTest(string cwd)
        {
            HandleWorkingDirectoryChanged(cwd);
        }

        /// <summary>
        /// Count of OSC 52 clipboard-write payloads that made it past the
        /// <see cref="TerminalSettings.AllowOsc52ClipboardWrite"/> gate and decoding in
        /// <see cref="CreateAndWireParser"/>'s <c>Parser.OnClipboardWrite</c> handler (issue #268).
        /// A synchronous seam for tests: the real path continues on to
        /// <c>Dispatcher.UIThread.Post</c> + <c>TermView.SetClipboardTextAsync</c>, which needs a
        /// live UI-thread TopLevel/Clipboard that headless tests do not provide, so this counter
        /// is what the settings-gate test asserts against instead.
        /// </summary>
        internal int ClipboardWriteAttemptsForTest => _clipboardWriteAttemptsForTest;

        /// <summary>
        /// Whether the pane has latched "this session emits OSC 133" - i.e. what
        /// <see cref="UpdateCommandAssistContext"/> would publish as <c>isShellIntegrated</c>.
        /// </summary>
        /// <remarks>
        /// A seam rather than an observation of the published context, because the publish is posted
        /// to the UI thread and the thing under test (the setting gate on
        /// <see cref="NoteShellIntegrationMarkObserved"/>) decides whether it is posted at all. A
        /// test asserting "nothing was published" against an async post would be asserting a
        /// timeout.
        /// </remarks>
        internal bool HasObservedShellIntegrationMarkForTest => _hasObservedShellIntegrationMark;

        /// <summary>
        /// The command text the pane would hand to Fix mode and the long-command notification. Read
        /// by the tests that pin what a <c>133;C</c> payload is and is not allowed to overwrite.
        /// </summary>
        internal string? LastRelevantCommandTextForTest => _lastRelevantCommandText;

        internal bool IsRemoteFilesSidebarVisibleForTest()
        {
            return RemoteFilesSidebarPresenter?.IsVisible == true;
        }

        internal bool IsRemoteFilesSidebarEntryAvailableForTest()
        {
            UpdateRemoteFilesSidebarEntryPointState();
            return MenuToggleRemoteFilesSidebar?.IsVisible == true &&
                   MenuToggleRemoteFilesSidebar.IsEnabled;
        }

        internal IReadOnlyList<string> GetSftpContextMenuItemNamesForTest()
        {
            if (RootGrid.ContextMenu?.Items is not IEnumerable<object> items)
            {
                return Array.Empty<string>();
            }

            MenuItem? sftpMenu = items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "SFTP");
            if (sftpMenu?.Items is null)
            {
                return Array.Empty<string>();
            }

            return sftpMenu.Items
                .OfType<MenuItem>()
                .Select(item => item.Name ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }

        internal string GetRemoteFilesSidebarCurrentPathForTest()
        {
            return _remoteFilesSidebarViewModel?.CurrentPath ?? string.Empty;
        }

        internal string? GetRemoteFilesSidebarJumpTargetForTest()
        {
            return _remoteFilesSidebarViewModel?.JumpToCurrentDirectoryPath;
        }

        internal bool IsRemoteFilesSidebarDisconnectedForTest()
        {
            return _remoteFilesSidebarViewModel?.IsDisconnected == true;
        }

        internal void HandleSessionExitForTesting(int code)
        {
            HandleSessionExit(Session, code);
        }

        private void HandleSessionExit(ITerminalSession? session, int code)
        {
            if (session != null && !ReferenceEquals(Session, session))
            {
                return;
            }

            LastExitCode = code;

            if (Profile?.Type == ConnectionType.SSH)
            {
                if (_remoteFilesSidebarViewModel?.IsOpen == true)
                {
                    _remoteFilesSidebarViewModel.MarkDisconnected();
                    UpdateRemoteFilesSidebarVisibility();
                    UpdateRemoteFilesSidebarEntryPointState();
                }

                WriteSshDisconnectedBanner(code);
            }

            ProcessExited?.Invoke(this, code);
        }

        private void WriteSshDisconnectedBanner(int code)
        {
            string exitCodeLine = code == 0
                ? string.Empty
                : $"[Exit code: {code}]\r\n";
            WriteBanner(
                $"\r\n[SSH session disconnected]\r\n{exitCodeLine}[Press Enter to reconnect]\r\n");
        }

        /// <summary>
        /// Strips control characters (C0 incl. ESC, DEL, and C1) from interpolated banner values.
        /// Banner text is parsed as terminal input, so remote- or profile-derived values such as
        /// SSH/spawn error messages could otherwise smuggle escape sequences that move the cursor,
        /// clear the screen, switch modes, or rewrite the title. Only printable content survives.
        /// </summary>
        internal static string SanitizeBannerValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                {
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Writes NovaTerminal-generated banner text (connection errors, disconnect/reconnect
        /// notices) into the terminal. Banners are routed through the ANSI parser rather than
        /// <see cref="TerminalBuffer.WriteContent"/> — which writes graphemes verbatim — so that
        /// embedded SGR color codes and CR/LF line breaks are interpreted, instead of leaving
        /// literal "[90m" garbage collapsed onto a single line. A fresh parser is used each time so
        /// the banner renders with a clean slate: it never inherits a partial escape sequence or
        /// accumulated SGR state from the (possibly just-disposed) session parser, and never shares
        /// mutable parser state with the background-thread session output pump.
        /// Interpolated values must be passed through <see cref="SanitizeBannerValue"/> first.
        /// </summary>
        private void WriteBanner(string text)
        {
            if (Buffer == null)
            {
                return;
            }

            new AnsiParser(Buffer).Process(text);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // Fallback: Ensure session is initialized if it wasn't yet (e.g. nested split timing)
            if (Session == null)
            {
                InitializeSession(ShellCommand, Profile, TermView.Cols, TermView.Rows);
            }

            // Force initial render availability
            Dispatcher.UIThread.Post(() =>
            {
                UpdateFocusVisuals(IsKeyboardFocusWithin);
                TermView.InvalidateVisual();
                UpdateCommandAssistOverlayPlacement();
            }, DispatcherPriority.Loaded);
        }

        public void Dispose()
        {
            // Synchronous teardown for UI-thread callers. MainWindow.DisposeControlTree
            // splits the two phases instead, so the (potentially blocking) session
            // teardown runs off the UI thread while the UI-affine detach stays on it.
            DetachFromUiThread()?.Dispose();
        }

        /// <summary>
        /// Detaches UI-affine state (control visibility, DispatcherTimer, event handlers)
        /// and transfers ownership of the underlying session to the caller for disposal.
        /// MUST be called on the UI thread. Returns <c>null</c> when already disposed or
        /// when the pane has no session. See #154: previously the whole Dispose ran on a
        /// worker thread with a swallowed catch, so a VerifyAccess throw in the UI-affine
        /// part aborted teardown before the session was disposed, leaking the PTY and its
        /// child shell.
        /// </summary>
        public ITerminalSession? DetachFromUiThread()
        {
            // Enforce the UI-thread contract BEFORE flipping _disposed: if this threw
            // mid-teardown after _disposed was set, every later Dispose() would return
            // early and the session would leak permanently — the exact bug this method
            // exists to prevent.
            Dispatcher.UIThread.VerifyAccess();

            if (_disposed) return null;
            _disposed = true;

            NovaTerminal.AgentHost.AgentSessionRegistry.Instance.Unregister(PaneId);

            CloseRemoteFilesSidebar();

            // Detach handlers on the reused TermView so a disposed pane stops reacting.
            if (_onTermViewResize != null) TermView.OnResize -= _onTermViewResize;
            if (_onTermViewMetricsChanged != null) TermView.MetricsChanged -= _onTermViewMetricsChanged;
            if (_onTermViewMetricsLayout != null) TermView.MetricsChanged -= _onTermViewMetricsLayout;

            if (Buffer != null)
            {
                Buffer.OnScreenSwitched -= OnBufferScreenSwitched;
            }
            _statusTimer?.Stop();
            _statusTimer = null;
            SftpService.Instance.JobUpdated -= Sftp_JobUpdated;
            if (Session != null)
            {
                ITerminalSession session = Session;
                UnregisterActiveSshSession(session);
                Session = null;
                _agentRegistration?.SetLifecycle(null);
                return session;
            }
            return null;
        }

        private static void RegisterActiveSshSession(ITerminalSession session, TerminalProfile? profile)
        {
            if (profile?.Type != ConnectionType.SSH || profile.SshBackendKind != SshBackendKind.Native)
            {
                return;
            }

            ActiveSshSessionRegistry.Instance.Register(new ActiveSshSessionDescriptor(
                session.Id,
                profile.Id,
                profile.SshBackendKind));
        }

        private static void UnregisterActiveSshSession(ITerminalSession session)
        {
            ActiveSshSessionRegistry.Instance.Unregister(session.Id);
        }

        private void Sftp_JobUpdated(object? sender, TransferJob job)
        {
            if (job.SessionId != Session?.Id) return;

            Dispatcher.UIThread.Post(() =>
            {
                var activeJobs = SftpService.Instance.Jobs
                    .Where(j => j.SessionId == Session?.Id && j.State == TransferState.Running)
                    .ToList();

                if (activeJobs.Count > 0)
                {
                    TransferJob primaryJob = activeJobs
                        .OrderByDescending(j => j.StartedAt)
                        .First();

                    SftpStatus.IsVisible = true;
                    SftpIcon.Text = activeJobs.Count > 1
                        ? "⇅"
                        : primaryJob.Direction == TransferDirection.Upload ? "⬆" : "⬇";
                    SftpText.Text = BuildRunningTransferStatus(primaryJob, activeJobs.Count);
                }
                else
                {
                    var lastJob = SftpService.Instance.Jobs
                        .Where(j => j.SessionId == Session?.Id)
                        .OrderByDescending(j => j.FinishedAt)
                        .FirstOrDefault();

                    if (lastJob != null && lastJob.FinishedAt > DateTime.Now.AddSeconds(-10))
                    {
                        SftpStatus.IsVisible = true;
                        SftpIcon.Text = lastJob.State switch
                        {
                            TransferState.Completed => "✅",
                            TransferState.Canceled => "⏹",
                            _ => "❌"
                        };
                        SftpText.Text = BuildCompletedTransferStatus(lastJob);
                    }
                    else
                    {
                        SftpStatus.IsVisible = false;
                    }
                }
            });
        }

        private static string BuildRunningTransferStatus(TransferJob job, int activeTransferCount)
        {
            string action = job.Direction == TransferDirection.Upload ? "Uploading" : "Downloading";
            string detail = job.BytesTotal > 0
                ? $" {Math.Round(job.Progress * 100)}%"
                : string.Empty;
            string prefix = activeTransferCount > 1 ? $"{activeTransferCount} transfers • " : string.Empty;
            return $"{prefix}{action} {job.DisplayName}{detail}";
        }

        private static string BuildCompletedTransferStatus(TransferJob job)
        {
            return job.State switch
            {
                TransferState.Completed => $"{(job.Direction == TransferDirection.Upload ? "Uploaded" : "Downloaded")} {job.DisplayName}",
                TransferState.Canceled => $"{(job.Direction == TransferDirection.Upload ? "Upload" : "Download")} canceled",
                TransferState.Failed when !string.IsNullOrWhiteSpace(job.LastError) => $"Transfer failed: {job.LastError}",
                TransferState.Failed => "Transfer failed",
                _ => "Transfer updated"
            };
        }

        private void UpdateForwardingStatus()
        {
            if (Profile == null || Profile.Forwards.Count == 0)
            {
                StatusBar.IsVisible = false;
                return;
            }

            bool anyChanges = false;
            foreach (var rule in Profile.Forwards)
            {
                var oldStatus = rule.Status;

                if (rule.Type == ForwardingType.Remote)
                {
                    // For now, assume remote is active if session is alive
                    rule.Status = (Session != null) ? ForwardingStatus.Active : ForwardingStatus.Stopped;
                }
                else
                {
                    bool isListening = CheckIfPortIsListening(rule);
                    if (isListening) rule.Status = ForwardingStatus.Active;
                    else if (Session != null) rule.Status = ForwardingStatus.Starting;
                    else rule.Status = ForwardingStatus.Stopped;
                }

                if (oldStatus != rule.Status) anyChanges = true;
            }

            if (anyChanges || !StatusBar.IsVisible)
            {
                UpdateStatusBarUI();
                (VisualRoot as MainWindow)?.UpdateTabVisuals();
            }
        }

        public async Task ExportSnapshotAsync(string format)
        {
            if (Buffer == null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string ext = format.ToLowerInvariant() switch
            {
                "png" => ".png",
                "ansi" => ".ansi",
                _ => ".txt"
            };

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export Terminal Snapshot ({format.ToUpperInvariant()})",
                SuggestedFileName = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
            });

            if (file == null) return;

            try
            {
                if (format.Equals("png", StringComparison.OrdinalIgnoreCase))
                {
                    var dpi = topLevel.RenderScaling;
                    var pixelSize = new PixelSize(
                        (int)Math.Ceiling(TermView.Bounds.Width * dpi),
                        (int)Math.Ceiling(TermView.Bounds.Height * dpi));

                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, new Vector(96 * dpi, 96 * dpi));
                    rtb.Render(TermView);

                    using var stream = await file.OpenWriteAsync();
                    rtb.Save(stream);
                }
                else if (format.Equals("ansi", StringComparison.OrdinalIgnoreCase))
                {
                    string data = NovaTerminal.VT.Export.TerminalExporter.ExportToAnsi(Buffer);
                    using var stream = await file.OpenWriteAsync();
                    using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(data);
                }
                else
                {
                    string data = NovaTerminal.VT.Export.TerminalExporter.ExportToPlainText(Buffer);
                    using var stream = await file.OpenWriteAsync();
                    using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(data);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Failed to export snapshot: {ex}");
            }
        }

        private void UpdateStatusBarUI()
        {
            if (Profile == null) return;
            StatusBar.IsVisible = true;
            StatusBarLabel.Text = $"SSH ▸ {Profile.Name} ▸";
            StatusBarRules.Children.Clear();

            foreach (var rule in Profile.Forwards)
            {
                var container = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };

                var icon = new TextBlock
                {
                    Text = "🔁",
                    FontSize = 10,
                    Foreground = rule.Status switch
                    {
                        ForwardingStatus.Active => Brushes.LimeGreen,
                        ForwardingStatus.Starting => Brushes.Yellow,
                        ForwardingStatus.Failed => Brushes.Red,
                        _ => Brushes.Gray
                    },
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };

                var txt = new TextBlock
                {
                    Text = rule.Type switch
                    {
                        ForwardingType.Local => $"L:{rule.LocalAddress}→{rule.RemoteAddress}",
                        ForwardingType.Remote => $"R:{rule.RemoteAddress}→{rule.LocalAddress}",
                        ForwardingType.Dynamic => $"D:{rule.LocalAddress}",
                        _ => ""
                    },
                    FontSize = 10,
                    Foreground = Brushes.White,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };

                container.Children.Add(icon);
                container.Children.Add(txt);
                StatusBarRules.Children.Add(container);
            }
        }

        private bool CheckIfPortIsListening(ForwardingRule rule)
        {
            try
            {
                string portStr = rule.LocalAddress;
                if (portStr.Contains(':')) portStr = portStr.Split(':').Last();
                if (!int.TryParse(portStr, out int port)) return false;

                var properties = IPGlobalProperties.GetIPGlobalProperties();
                var listeners = properties.GetActiveTcpListeners();
                return listeners.Any(l => l.Port == port);
            }
            catch { return false; }
        }

        private void ApplyShellIntegrationLaunchPlan(
            TerminalProfile? profile,
            ref string effectiveShell,
            ref string args,
            string startingDirectory)
        {
            if (_settings == null || !_settings.CommandAssistShellIntegrationEnabled)
            {
                return;
            }

            string shellKind = DetermineShellKind(effectiveShell);
            ShellIntegrationRegistry registry = RequireCommandAssistServices().ShellIntegrationRegistry;
            IShellIntegrationProvider? provider = registry.GetProvider(shellKind, profile?.Command);
            if (provider == null)
            {
                return;
            }

            if (!_settings.CommandAssistPowerShellIntegrationEnabled &&
                provider is PowerShellShellIntegrationProvider)
            {
                return;
            }

            ShellIntegrationLaunchPlan plan;
            try
            {
                plan = provider.CreateLaunchPlan(effectiveShell, args, startingDirectory);
            }
            catch
            {
                return;
            }

            if (!plan.IsIntegrated)
            {
                return;
            }

            effectiveShell = plan.ShellCommand;
            args = plan.ShellArguments ?? string.Empty;
            _isShellIntegrationActive = true;
            _shellIntegrationEnvOverrides = plan.EnvironmentOverrides;
            ArmShellIntegrationTracker();
        }

        /// <summary>
        /// Arms the translator that turns raw OSC 133 parser callbacks into the ordered
        /// <see cref="ShellIntegrationEvent"/> stream Command Assist consumes.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from <c>_isShellIntegrationActive</c>, which the injection caller
        /// sets and which means something narrower: that <em>we</em> injected a bootstrap into this
        /// shell. Arming the translator only says the events will be delivered if they arrive - it
        /// is inert on a shell that emits no marks, since every tracker entry point is reached only
        /// from a parser mark callback. V2 Phase 2b is where a session we did not instrument arms it
        /// too; see <see cref="ArmRemoteShellIntegrationTracker"/>. Internal so a headless test can
        /// drive the real parser to tracker to dispatcher to controller path without spawning a
        /// shell.
        /// </remarks>
        internal void ArmShellIntegrationTracker()
        {
            _shellLifecycleTracker = new ShellLifecycleTracker();
            _shellLifecycleTracker.EventObserved += OnShellIntegrationEventObserved;
        }

        /// <summary>
        /// Arms the OSC 133 translator for an SSH session, so that a remote shell the user has
        /// instrumented themselves (see <c>docs/command-assist/RemoteShellIntegration.md</c>) gets
        /// the same Command Assist treatment a local integrated shell does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why unconditionally, before any mark has been seen.</b> The alternative - arm lazily on
        /// the first observed mark - loses that mark. <c>133;A</c> and the first <c>133;B</c> arrive
        /// with the very first remote prompt, and the <c>B</c> is what opens the command-input window
        /// the grid reader is gated on; a tracker armed after it would leave the first command line
        /// unreadable for no gain. Arming costs one object and one event subscription on a session
        /// that may never emit a mark, and produces no events until one does.
        /// </para>
        /// <para>
        /// <b>Why it cannot regress markless SSH.</b> Every path into
        /// <see cref="ShellLifecycleTracker"/> is a parser mark callback. A remote host with no
        /// snippet installed emits no OSC 133, so no event is ever dispatched, no
        /// <c>HasObservedShellIntegrationMarker</c> is set, and the heuristic Enter-time capture and
        /// the conservative markless anchoring stack behave exactly as they did before. The
        /// agent-status machinery is not affected either way: it hangs off the parser callbacks
        /// directly, never off the tracker.
        /// </para>
        /// <para>
        /// <b>Gated on the shell-integration setting</b>, which is the same switch that decides
        /// whether we inject locally. It is the user's "do not participate in the OSC 133 contract"
        /// control, and a remote host is exactly where they cannot simply uninstall the emitter.
        /// (Mark-based overlay <em>anchoring</em> is deliberately not gated on it - that path reads
        /// the parser's mark directly and predates this switch's remote meaning.)
        /// </para>
        /// <param name="profile">
        /// The profile being connected. Passed explicitly because
        /// <see cref="InitializeSessionCore"/> runs before <see cref="Profile"/> has necessarily
        /// caught up with the session being built; falls back to <see cref="Profile"/> for callers
        /// (and tests) that have already published it.
        /// </param>
        /// </remarks>
        internal void ArmRemoteShellIntegrationTracker(TerminalProfile? profile = null)
        {
            profile ??= Profile;
            if (profile?.Type != ConnectionType.SSH)
            {
                return;
            }

            if (_settings != null && !_settings.CommandAssistShellIntegrationEnabled)
            {
                return;
            }

            ArmShellIntegrationTracker();
        }

        private void OnShellIntegrationEventObserved(ShellIntegrationEvent shellEvent)
        {
            if (shellEvent.Type == ShellIntegrationEventType.CommandAccepted &&
                !string.IsNullOrWhiteSpace(shellEvent.CommandText))
            {
                _lastRelevantCommandText = shellEvent.CommandText.Trim();
            }

            // OSC 133;B (CommandStarted) used to be dropped here as a no-op: nothing consumed it,
            // and B fires once per prompt AND once per prompt repaint, so forwarding it only
            // queued dead work onto the serialized dispatcher ahead of events that did something.
            //
            // Phase 1c gave it a job. B is what opens Command Assist's command-input window
            // (AssistSessionContext.IsAcceptingCommandInput), and that window is the lifecycle gate
            // on reading the command line out of the grid: with the event dropped, the gate would
            // never open and grid-truth query state would be dead on arrival. The early-out is
            // therefore gone, and the repaint cost -- one dispatcher hop per prompt paint, doing an
            // idempotent bool set and a marker observation -- is the price of the gate.
            _ = _shellIntegrationEventDispatcher.EnqueueAsync(() => HandleShellIntegrationEventAsync(shellEvent));
        }

        /// <summary>
        /// Reads the live command line out of the terminal grid: the cells between the newest
        /// <c>OSC 133;B</c> mark and the cursor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Phase 1b seam. Combines the three things the reader needs and the pane is the
        /// only place that has all of: the newest mark, the buffer, and the buffer's read lock
        /// (taken by <see cref="GridQueryReader"/> itself).
        /// </para>
        /// <para>
        /// <b>Lifecycle.</b> The mark is dropped on <c>OSC 133;D</c> (command finished), so
        /// between one command's end and the next prompt's <c>B</c> this returns <c>false</c>
        /// rather than serving that command's output as a command line. It is deliberately kept
        /// across <c>OSC 133;C</c>: C fires the instant the user submits, while the input line is
        /// still on screen and still exactly what the mark describes.
        /// <see cref="GridQueryReader.MaxSpanRows"/> remains as a backstop for shells that emit
        /// <c>B</c> without a matching <c>D</c>.
        /// </para>
        /// <para>
        /// <b>The mark is only half the gate.</b> Whether the cells between it and the cursor are a
        /// command line the user is editing or the output of a command that already ran is a
        /// lifecycle fact, and Command Assist holds it
        /// (<c>AssistSessionContext.IsAcceptingCommandInput</c>, opened by <c>133;B</c> and closed
        /// by <c>133;C</c>). This seam answers only "can the grid be read from the newest mark";
        /// <see cref="TryReadAssistQuerySnapshot"/> is what the orchestrator calls, and the
        /// orchestrator applies the gate before calling it.
        /// </para>
        /// </remarks>
        /// <returns><c>false</c> when there is no live mark or the grid cannot be read.</returns>
        internal bool TryGetGridCommandLine(out GridCommandLine line)
        {
            line = default;

            var buffer = Buffer;
            if (buffer == null)
            {
                return false;
            }

            ShellIntegrationMark? mark;
            lock (_commandStartMarkGate)
            {
                mark = _latestCommandStartMark;
            }

            return mark is ShellIntegrationMark live
                && GridQueryReader.TryReadCommandLine(buffer, live, out line);
        }

        /// <summary>
        /// The App-boundary mapping: <see cref="GridCommandLine"/> (VT) to
        /// <see cref="AssistQuerySnapshot"/> (Command Assist). This is the provider handed to the
        /// controller, and the only place the two type systems meet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Callable from any thread, and it is: the suggestion orchestrator invokes it from the
        /// worker its refresh pass runs on, deliberately, so the read lands behind the queue hop
        /// rather than on the keystroke that triggered it. Both things it touches are safe for
        /// that - the mark is read under <c>_commandStartMarkGate</c> and
        /// <see cref="GridQueryReader"/> takes the buffer's own read lock.
        /// </para>
        /// <para>
        /// The span's <c>StartRow</c>/<c>EndRow</c> are dropped rather than carried across. Nothing
        /// on the far side has a use for buffer coordinates, and Phase 2's anchoring work will take
        /// the <c>133;A</c> row through the anchor calculator instead.
        /// </para>
        /// </remarks>
        internal AssistQuerySnapshot? TryReadAssistQuerySnapshot()
        {
            return TryGetGridCommandLine(out GridCommandLine line)
                ? new AssistQuerySnapshot(
                    Text: line.Text,
                    CursorOffset: line.CursorOffset,
                    IsMultiline: line.IsMultiline,
                    RightPromptTrimmed: line.RightPromptTrimmed)
                : null;
        }

        /// <summary>
        /// The query as Command Assist actually sees it: the grid read with the lifecycle gate
        /// applied.
        /// </summary>
        /// <remarks>
        /// Deliberately distinct from <see cref="TryReadAssistQuerySnapshot"/>, which is the raw
        /// seam and is ungated on purpose. The <c>133;B</c> mark survives <c>133;C</c> (it is only
        /// dropped on <c>D</c>), so the seam keeps answering while a command runs, and the thing
        /// that says its answer must not be believed is
        /// <c>AssistSessionContext.IsAcceptingCommandInput</c>, applied inside
        /// <c>SuggestionOrchestrator</c>. A test asserting "the gate closed" has to ask on this side
        /// of it; asking the seam would assert the mark lifecycle instead and pass for the wrong
        /// reason.
        /// </remarks>
        internal AssistQuerySnapshot? TryReadGatedAssistQuerySnapshotForTest() =>
            _commandAssistController?.TryReadQuerySnapshot();

        internal async Task HandleCommandAssistCompletionAsync(int? exitCode)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            // Only when nothing else will patch the entry. An armed tracker turns this same OSC 133;D
            // into a CommandFinished event that patches the exit code *and* the duration, and it is
            // the better of the two; running both means the first one clears the pending entry and
            // the second silently does nothing, which loses the duration.
            //
            // Keyed on the tracker rather than on _isShellIntegrationActive since V2 Phase 2b: for a
            // remote session the latter is false while the tracker is armed, and the old condition
            // would have raced the structured patch on every SSH command.
            if (_shellLifecycleTracker == null)
            {
                await _commandAssistController.HandleCommandFinishedAsync(exitCode);
            }

            if (!exitCode.HasValue || exitCode.Value == 0 || Buffer?.IsAltScreenActive == true)
            {
                return;
            }

            string commandText = _lastRelevantCommandText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return;
            }

            var context = new CommandFailureContext(
                CommandText: commandText,
                ExitCode: exitCode,
                ShellKind: DetermineShellKind(Session?.ShellCommand ?? ShellCommand),
                WorkingDirectory: CurrentWorkingDirectory,
                ErrorOutput: null,
                IsRemote: Profile?.Type == ConnectionType.SSH,
                SelectedText: null);

            await _commandAssistController.HandleCommandFailureAsync(context);
        }

        private async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            try
            {
                await _commandAssistController.HandleShellIntegrationEventAsync(shellEvent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Shell integration event handling failed: {ex.Message}");
            }
        }

        private readonly record struct CommandAssistSurfaceSizing(
            double BubbleWidth,
            double BubbleHeight,
            double PopupWidth,
            double PopupHeight);

        private sealed class TestRemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
        {
            public Task<RemoteSidebarListingResult> ListDirectoryAsync(
                Guid profileId,
                Guid sessionId,
                string remotePath,
                CancellationToken cancellationToken)
            {
                string resolvedPath = string.IsNullOrWhiteSpace(remotePath) ? "~" : remotePath;
                return Task.FromResult(RemoteSidebarListingResult.Success(
                    resolvedPath,
                    Array.Empty<RemoteSidebarEntry>()));
            }
        }
    }
}
