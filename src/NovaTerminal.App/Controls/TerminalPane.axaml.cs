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
        private bool _isShellIntegrationActive;
        private IReadOnlyDictionary<string, string>? _shellIntegrationEnvOverrides;
        private readonly OrderedAsyncEventDispatcher _shellIntegrationEventDispatcher = new();
        private readonly CommandAssistAnchorCalculator _commandAssistAnchorCalculator = new();
        private string? _lastRelevantCommandText;
        private CommandAssistBarViewModel? _boundCommandAssistViewModel;
        private string? _lastCommandAssistAnchorDiagnosticSignature;
        private string? _lastCommandAssistAnchorAppliedSignature;
        private string? _lastCommandAssistAnchorCorrectionSignature;
        private bool _suppressSshAssistOverlayUntilSettled;
        private int _sshAssistCorrectionPassCount;
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
            TermView.TextInputObserved += text =>
            {
                if (EnsureCommandAssistInitialized())
                {
                    _commandAssistController?.HandleTextInput(text);
                }
            };
            TermView.BackspaceObserved += () =>
            {
                if (EnsureCommandAssistInitialized())
                {
                    _commandAssistController?.HandleBackspace();
                }
            };
            TermView.EnterObserved += OnCommandAssistEnterObserved;
            TermView.PasteObserved += text =>
            {
                if (EnsureCommandAssistInitialized())
                {
                    _commandAssistController?.HandlePastedText(text);
                }
            };

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
                action =>
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
            float fallbackCellHeight = TermView.Metrics.CellHeight > 0 ? TermView.Metrics.CellHeight : 18;
            int fallbackVisibleRows = TermView.Rows > 0 ? TermView.Rows : 1;
            CommandAssistSurfaceSizing sizing = CalculateCommandAssistSurfaceSizing(paneWidth, paneHeight);
            bool hasReliablePromptAnchor = IsCommandAssistPromptAnchorReliable(promptHint);
            float anchorCellHeight = promptHint?.CellHeight ?? fallbackCellHeight;
            int hintCursorRow = promptHint?.VisibleCursorVisualRow ?? 0;
            int hintVisibleRows = promptHint?.VisibleRows ?? fallbackVisibleRows;
            int paneEstimatedVisibleRows = anchorCellHeight > 0
                ? Math.Max(1, (int)Math.Floor(paneHeight / anchorCellHeight))
                : hintVisibleRows;
            bool shouldUsePaneEstimatedRows = Profile?.Type == ConnectionType.SSH &&
                                              !hasReliablePromptAnchor &&
                                              paneEstimatedVisibleRows > hintVisibleRows;
            int anchorVisibleRows = shouldUsePaneEstimatedRows ? paneEstimatedVisibleRows : hintVisibleRows;
            int anchorCursorRow = Math.Clamp(hintCursorRow, 0, Math.Max(0, anchorVisibleRows - 1));
            bool shouldSuppress = ShouldSuppressConservativeRemoteAssist(promptHint, hasReliablePromptAnchor, paneHeight);
            if (shouldSuppress)
            {
                LogCommandAssistAnchorDiagnostics(
                    paneWidth,
                    paneHeight,
                    hasReliablePromptAnchor,
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
                HasReliablePromptAnchor: hasReliablePromptAnchor));
            LogCommandAssistAnchorDiagnostics(
                paneWidth,
                paneHeight,
                hasReliablePromptAnchor,
                promptHint,
                anchorCellHeight,
                anchorCursorRow,
                anchorVisibleRows,
                shouldSuppress,
                layout);
            return layout;
        }

        private void LogCommandAssistAnchorDiagnostics(
            double paneWidth,
            double paneHeight,
            bool hasReliablePromptAnchor,
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
                : $"bubbleY={layout.BubbleRect.Y:F0},bubbleBottom={layout.BubbleRect.Bottom:F0},promptY={layout.PromptRect.Y:F0},usesPrompt={layout.UsesPromptAnchor}";
            string signature =
                $"pw={paneWidth:F0},ph={paneHeight:F0},tw={TermView.Bounds.Width:F0},th={TermView.Bounds.Height:F0},rel={hasReliablePromptAnchor},sup={shouldSuppress},hintRow={hintCursorRow},hintRows={hintVisibleRows},cell={anchorCellHeight:F1},anchorRow={anchorCursorRow},anchorRows={anchorVisibleRows},vmVis={_boundCommandAssistViewModel?.IsVisible == true},{layoutState}";
            if (string.Equals(signature, _lastCommandAssistAnchorDiagnosticSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastCommandAssistAnchorDiagnosticSignature = signature;
            TerminalLogger.Log($"[AssistAnchor][SSH] {signature}");
        }

        private bool ShouldSuppressConservativeRemoteAssist(
            CommandAssistPromptHint? promptHint,
            bool hasReliablePromptAnchor,
            double paneHeight)
        {
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

        private bool IsCommandAssistPromptAnchorReliable(CommandAssistPromptHint? promptHint)
        {
            if (!promptHint.HasValue)
            {
                return false;
            }

            // SSH sessions currently stay on the heuristic path, so cursor-row hints are not
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
            if (!shouldShowOverlayHost)
            {
                _suppressSshAssistOverlayUntilSettled = false;
                _sshAssistCorrectionPassCount = 0;
            }

            if (CommandAssistOverlayHost != null)
            {
                CommandAssistOverlayHost.IsVisible = shouldShowOverlayHost;
                CommandAssistOverlayHost.Opacity = shouldShowOverlayHost && !_suppressSshAssistOverlayUntilSettled ? 1.0 : 0.0;
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

        private void ScheduleCommandAssistPlacementCorrection(CommandAssistAnchorLayout layout)
        {
            if (Profile?.Type != ConnectionType.SSH || _boundCommandAssistViewModel?.IsVisible != true)
            {
                return;
            }

            void CorrectPlacement()
            {
                if (CommandAssistBubble == null || CommandAssistOverlayHost == null || !CommandAssistOverlayHost.IsVisible)
                {
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
                    _sshAssistCorrectionPassCount = 0;
                    if (_suppressSshAssistOverlayUntilSettled)
                    {
                        _suppressSshAssistOverlayUntilSettled = false;
                        CommandAssistOverlayHost.Opacity = 1.0;
                    }

                    return;
                }

                _suppressSshAssistOverlayUntilSettled = true;
                CommandAssistOverlayHost.Opacity = 0.0;

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
                    TerminalLogger.Log("[AssistAnchor][SSH][Corrected] max-pass reached; showing overlay with best-known anchor.");
                    return;
                }

                _sshAssistCorrectionPassCount++;

                // Re-evaluate on the next render pass; keep host hidden until settled.
                Dispatcher.UIThread.Post(UpdateCommandAssistOverlayPlacement, DispatcherPriority.Render);
            }

            Dispatcher.UIThread.Post(CorrectPlacement, DispatcherPriority.Render);
        }

        private void OnBufferScreenSwitched(bool isAltScreen)
        {
            Dispatcher.UIThread.Post(() => HandleAltScreenChanged(isAltScreen));
        }

        private void HandleAltScreenChanged(bool isAltScreen)
        {
            _agentRegistration?.StatusMachine.NotifyAltScreenChanged(isAltScreen);
            _commandAssistController?.HandleAltScreenChanged(isAltScreen);
            UpdateRemoteFilesSidebarVisibility();
            UpdateRemoteFilesSidebarEntryPointState();
        }

        private void OnCommandAssistEnterObserved()
        {
            _ = HandleCommandAssistEnterObservedAsync();
        }

        private async Task HandleCommandAssistEnterObservedAsync()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            try
            {
                string currentQuery = _commandAssistController.ViewModel.QueryText;
                if (!string.IsNullOrWhiteSpace(currentQuery))
                {
                    _lastRelevantCommandText = currentQuery.Trim();
                }

                await _commandAssistController.HandleEnterAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Command Assist enter handling failed: {ex.Message}");
            }
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
                isShellIntegrated: _isShellIntegrationActive);
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

        internal bool TryHandleCommandAssistKey(Key key, KeyModifiers modifiers)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            CommandAssistController? controller = _commandAssistController;
            bool isAssistVisible = controller?.ViewModel.IsVisible == true;
            if (!CommandAssistKeyRouter.IsAssistOwnedKey(
                    isAssistVisible,
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

            if (key == Key.Down)
            {
                controller?.MoveSelectionDown();
                return true;
            }

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
            return TryHandleCommandAssistKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift);
        }

        private bool TryInsertSelectedCommandAssistSuggestion()
        {
            if (_commandAssistController == null || Session == null)
            {
                return false;
            }

            string existingQuery = _commandAssistController.ViewModel.QueryText;
            if (!_commandAssistController.TryAcceptSelection(out string? insertionText) || insertionText == null)
            {
                return false;
            }

            if (!CommandAssistInsertionPlanner.TryCreateInsertion(existingQuery, insertionText, out string? textToSend) ||
                string.IsNullOrEmpty(textToSend))
            {
                return false;
            }

            _lastRelevantCommandText = insertionText;
            Session.SendInput(textToSend);
            return true;
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
                _shellLifecycleTracker?.HandlePromptReady();
                _agentRegistration?.StatusMachine.NotifyPromptReady();
            };
            Parser.OnCommandAccepted += commandText =>
            {
                _lastRelevantCommandText = commandText?.Trim();
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
            _shellIntegrationEnvOverrides = null;

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
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateScrollUI();
                    OutputReceived?.Invoke(this);
                });
            };

            // Wire up Parser responses (e.g. DA1)
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
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                _lastRelevantCommandText = text.Trim();
            }

            _commandAssistController?.HandlePastedText(text);
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
            _shellLifecycleTracker = new ShellLifecycleTracker();
            _shellLifecycleTracker.EventObserved += OnShellIntegrationEventObserved;
        }

        private void OnShellIntegrationEventObserved(ShellIntegrationEvent shellEvent)
        {
            if (shellEvent.Type == ShellIntegrationEventType.CommandAccepted &&
                !string.IsNullOrWhiteSpace(shellEvent.CommandText))
            {
                _lastRelevantCommandText = shellEvent.CommandText.Trim();
            }

            // OSC 133;B (CommandStarted) is dropped unconditionally by
            // CommandAssistController.HandleShellIntegrationEventAsync, and B fires once per
            // prompt AND once per prompt repaint -- so forwarding it only queues no-op work
            // onto the serialized dispatcher, ahead of events that do something. The
            // "shell integration is live" flag the controller would set from it is already
            // set by the PromptReady (OSC 133;A) that precedes every B.
            // Phase 1b, when the mark position starts feeding the grid reader, has to remove
            // this early-out.
            if (shellEvent.Type == ShellIntegrationEventType.CommandStarted)
            {
                return;
            }

            _ = _shellIntegrationEventDispatcher.EnqueueAsync(() => HandleShellIntegrationEventAsync(shellEvent));
        }

        internal async Task HandleCommandAssistCompletionAsync(int? exitCode)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            if (!_isShellIntegrationActive)
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
