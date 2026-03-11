using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia;
using NovaTerminal.Core;
using Avalonia.Controls.Presenters;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Linq;
using Avalonia.Controls.Shapes;
using Avalonia.Automation;
using Avalonia.Platform.Storage;
using System.ComponentModel;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using NovaTerminal.CommandAssist.ShellIntegration.Runtime;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Sessions;

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

    public partial class TerminalPane : UserControl, IDisposable
    {
        public ITerminalSession? Session { get; private set; }
        public TerminalBuffer? Buffer { get; private set; }
        public AnsiParser? Parser { get; private set; }
        public string ShellCommand { get; private set; } = string.Empty;
        public string ShellArgs { get; private set; } = string.Empty;
        public TerminalProfile? Profile { get; private set; }
        public Guid PaneId { get; set; } = Guid.NewGuid();

        public event Action<TerminalPane, TransferDirection, TransferKind>? RequestSftpTransfer;
        public event Action<bool>? RecordingStateChanged;
        public event Action<TerminalPane, string>? WorkingDirectoryChanged;
        public event Action<TerminalPane, string>? TitleChanged;
        public event Action<TerminalPane, PaneAction>? PaneActionRequested;
        public event Action<TerminalPane>? OutputReceived;
        public event Action<TerminalPane>? BellReceived;
        public event Action<TerminalPane>? CommandStarted;
        public event Action<TerminalPane, int?>? CommandFinished;
        public event Action<TerminalPane, int>? ProcessExited;

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;
        private DispatcherTimer? _statusTimer;
        private bool _hasUserInteraction;
        private readonly SshDiagnosticsLevel _sshDiagnosticsLevel;
        private string? _pendingPasteFilePath;
        private string? _pendingEscapedPath;
        private CommandAssistController? _commandAssistController;
        private ShellLifecycleTracker? _shellLifecycleTracker;
        private bool _isShellIntegrationActive;
        private readonly OrderedAsyncEventDispatcher _shellIntegrationEventDispatcher = new();
        private readonly CommandAssistAnchorCalculator _commandAssistAnchorCalculator = new();
        private string? _lastRelevantCommandText;
        private CommandAssistBarViewModel? _boundCommandAssistViewModel;
        private readonly CommandAssistBubbleViewModel _hiddenCommandAssistBubbleViewModel = new() { IsVisible = false };
        private readonly CommandAssistPopupViewModel _hiddenCommandAssistPopupViewModel = new(new ObservableCollection<CommandAssistSuggestionItemViewModel>()) { IsVisible = false };
        private const double CommandAssistBubbleWidth = 420;
        private const double CommandAssistBubbleHeight = 36;
        private const double CommandAssistPopupWidth = 520;
        private const double CommandAssistPopupHeight = 220;
        private const double CompactPopupWidthThreshold = 420;
        private const double CompactPopupHeightThreshold = 180;
        internal CommandAssistBarViewModel? CommandAssistViewModel => _commandAssistController?.ViewModel;

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
                }
            }
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

            if (Session.IsRecording)
            {
                Session.StopRecording();
                Buffer?.WriteContent("\r\n[Nova] Recording stopped.\r\n", false);
            }
            else
            {
                try
                {
                    string recDir = AppPaths.RecordingsDirectory;
                    if (!System.IO.Directory.Exists(recDir)) System.IO.Directory.CreateDirectory(recDir);

                    string filename = $"nova_rec_{DateTime.Now:yyyyMMdd_HHmmss}.rec";
                    string path = System.IO.Path.Combine(recDir, filename);

                    Session.StartRecording(path);
                    Buffer?.WriteContent($"\r\n[Nova] Recording started: {filename}\r\n", false);
                }
                catch (Exception ex)
                {
                    Buffer?.WriteContent($"\r\n[Nova] Failed to start recording: {ex.Message}\r\n", false);
                }
            }
            RecordingStateChanged?.Invoke(IsRecording);
        }

        public void UpdateProfile(TerminalProfile profile)
        {
            Profile = profile;
            TermView.ShellOverride = profile.ShellOverride;
            UpdateCommandAssistContext();
        }

        public Control ActiveControl => TermView;

        public TerminalPane()
        {
            InitializeComponent();
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(null, null, c, r);
            SetupCommon();
        }

        public TerminalPane(string shell)
        {
            InitializeComponent();
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r);
            SetupCommon();
        }

        public TerminalPane(string shell, string args)
        {
            InitializeComponent();
            _sshDiagnosticsLevel = SshDiagnosticsLevel.None;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r, args);
            SetupCommon();
        }

        public TerminalPane(TerminalProfile profile)
            : this(profile, SshDiagnosticsLevel.None)
        {
        }

        public TerminalPane(TerminalProfile profile, SshDiagnosticsLevel sshDiagnosticsLevel)
        {
            Profile = profile;
            InitializeComponent();
            _sshDiagnosticsLevel = sshDiagnosticsLevel;
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.ShellOverride = profile.ShellOverride;
            TermView.Ready += (c, r) => InitializeSession(profile.Command, profile, c, r);
            SetupCommon();
        }

        private void SetupCommon()
        {
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
            TermView.MetricsChanged += (cw, ch) =>
            {
                UpdateMinimumSizeConstraints();
                UpdateCommandAssistOverlayPlacement();
            };
            TermView.CommandAssistAnchorHintChanged += () => UpdateCommandAssistOverlayPlacement();
            SizeChanged += (_, _) => UpdateCommandAssistOverlayPlacement();

            // Load Settings
            ApplySettings(TerminalSettings.Load());
            InitializeCommandAssist();
            UpdateMinimumSizeConstraints();
            AutomationProperties.SetName(TermView, "Terminal Pane");
            AutomationProperties.SetName(this, "Terminal Pane");

            // Smart Paste Action setup
            TermView.TextFileDropped += (s, args) =>
            {
                _pendingPasteFilePath = args.FilePath;
                _pendingEscapedPath = args.EscapedPath;
                ToastMessageText.Text = System.IO.Path.GetFileName(args.FilePath);
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
                        NovaTerminal.Core.Input.TerminalInputSender.SendBracketedPaste(Session, content);
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

                var sftpMenu = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "SFTP");
                if (sftpMenu != null)
                {
                    foreach (var sub in sftpMenu.Items.OfType<MenuItem>())
                    {
                        if (sub.Name == "MenuUploadFile") sub.Click += (s, e) => RequestSftpTransfer?.Invoke(this, TransferDirection.Upload, TransferKind.File);
                        if (sub.Name == "MenuUploadFolder") sub.Click += (s, e) => RequestSftpTransfer?.Invoke(this, TransferDirection.Upload, TransferKind.Folder);
                        if (sub.Name == "MenuDownloadFile") sub.Click += (s, e) => RequestSftpTransfer?.Invoke(this, TransferDirection.Download, TransferKind.File);
                        if (sub.Name == "MenuDownloadFolder") sub.Click += (s, e) => RequestSftpTransfer?.Invoke(this, TransferDirection.Download, TransferKind.Folder);
                    }
                }

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
            _commandAssistController = new CommandAssistController(
                CommandAssistInfrastructure.GetHistoryStore(settings),
                CommandAssistInfrastructure.GetSecretsFilter(),
                CommandAssistInfrastructure.GetSuggestionEngine(),
                CommandAssistInfrastructure.GetSnippetStore(),
                CommandAssistInfrastructure.GetCommandDocsProvider(),
                CommandAssistInfrastructure.GetRecipeProvider(),
                CommandAssistInfrastructure.GetErrorInsightService(),
                modeRouter: null,
                resultBuilder: null,
                action => Dispatcher.UIThread.Post(action));

            BindCommandAssistViews(_commandAssistController.ViewModel);

            _commandAssistController.HandleAltScreenChanged(Buffer?.IsAltScreenActive ?? false);
            UpdateCommandAssistContext();

            TermView.TextInputObserved += text =>
            {
                if (IsCommandAssistFeatureEnabled())
                {
                    _commandAssistController?.HandleTextInput(text);
                }
            };
            TermView.BackspaceObserved += () =>
            {
                if (IsCommandAssistFeatureEnabled())
                {
                    _commandAssistController?.HandleBackspace();
                }
            };
            TermView.EnterObserved += OnCommandAssistEnterObserved;
            TermView.PasteObserved += text =>
            {
                if (IsCommandAssistFeatureEnabled())
                {
                    _commandAssistController?.HandlePastedText(text);
                }
            };

            if (Buffer != null)
            {
                Buffer.OnScreenSwitched += OnBufferScreenSwitched;
            }
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

            return _commandAssistAnchorCalculator.Calculate(new CommandAssistAnchorRequest(
                PaneWidth: paneWidth,
                PaneHeight: paneHeight,
                CellHeight: promptHint?.CellHeight ?? fallbackCellHeight,
                CursorVisualRow: promptHint?.VisibleCursorVisualRow ?? 0,
                VisibleRows: promptHint?.VisibleRows ?? fallbackVisibleRows,
                BubbleWidth: sizing.BubbleWidth,
                BubbleHeight: sizing.BubbleHeight,
                PopupWidth: sizing.PopupWidth,
                PopupHeight: sizing.PopupHeight,
                HasReliablePromptAnchor: hasReliablePromptAnchor));
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
            if (layout == null)
            {
                return;
            }

            double paneHeight = TermView.Bounds.Height > 0 ? TermView.Bounds.Height : Bounds.Height;

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
        }

        private void OnBufferScreenSwitched(bool isAltScreen)
        {
            Dispatcher.UIThread.Post(() => _commandAssistController?.HandleAltScreenChanged(isAltScreen));
        }

        private void OnCommandAssistEnterObserved()
        {
            _ = HandleCommandAssistEnterObservedAsync();
        }

        private async Task HandleCommandAssistEnterObservedAsync()
        {
            if (!IsCommandAssistFeatureEnabled() || _commandAssistController == null)
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
            if (!CommandAssistKeyRouter.IsAssistOwnedKey(isAssistVisible, key, modifiers))
            {
                return false;
            }

            bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
            bool isShift = (modifiers & KeyModifiers.Shift) != 0;

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

            if (key == Key.Tab)
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

        private static string DetermineShellKind(string? shellCommand)
        {
            if (string.IsNullOrWhiteSpace(shellCommand))
            {
                return "unknown";
            }

            if (shellCommand.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                shellCommand.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "pwsh";
            }

            if (shellCommand.Contains("cmd", StringComparison.OrdinalIgnoreCase))
            {
                return "cmd";
            }

            if (shellCommand.Contains("bash", StringComparison.OrdinalIgnoreCase) ||
                shellCommand.Contains("zsh", StringComparison.OrdinalIgnoreCase) ||
                shellCommand.Contains("sh", StringComparison.OrdinalIgnoreCase))
            {
                return "posix";
            }

            return "unknown";
        }

        private void InitializeSession(string? shell, TerminalProfile? profile, int cols, int rows, string? explicitArgs = null)
        {
            if (Session != null || Buffer == null) return;

            if (cols <= 0 || rows <= 0) return;

            // Update buffer to match view exactly before starting PTY
            Buffer.Resize(cols, rows);
            Parser = new AnsiParser(Buffer);

            Parser.OnBell += () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    TermView.TriggerBell();
                    BellReceived?.Invoke(this);
                });
            };
            Parser.OnWorkingDirectoryChanged += cwd =>
            {
                _shellLifecycleTracker?.HandleWorkingDirectoryChanged(cwd);
                Dispatcher.UIThread.Post(() =>
                {
                    CurrentWorkingDirectory = cwd;
                    UpdateCommandAssistContext();
                    WorkingDirectoryChanged?.Invoke(this, cwd);
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
            };
            Parser.OnCommandAccepted += commandText =>
            {
                _lastRelevantCommandText = commandText?.Trim();
                _shellLifecycleTracker?.HandleCommandAccepted(commandText);
            };
            Parser.OnCommandStarted += () =>
            {
                _shellLifecycleTracker?.HandleCommandStarted();
                Dispatcher.UIThread.Post(() =>
                {
                    LastExitCode = null;
                    CommandStarted?.Invoke(this);
                });
            };
            Parser.OnCommandFinished += exitCode =>
            {
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
            };

            // Sync initial metrics
            float cw = TermView.Metrics.CellWidth;
            float ch = TermView.Metrics.CellHeight;
            if (cw > 0) Parser.CellWidth = cw;
            if (ch > 0) Parser.CellHeight = ch;

            // Setup Session
            string effectiveShell = shell ?? ShellHelper.GetDefaultShell();
            string args = explicitArgs ?? profile?.Arguments ?? "";
            _shellLifecycleTracker = null;
            _isShellIntegrationActive = false;

            // Update SFTP Menu Visibility
            // If it's not an SSH session, detach the context menu entirely to avoid "tiny empty box" artifacts
            if (profile == null || profile.Type != ConnectionType.SSH)
            {
                RootGrid.ContextMenu = null;
            }

            string startingDir = profile?.StartingDirectory ?? "";
            Session = null;
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
                        var connectionService = new NovaTerminal.Services.Ssh.SshConnectionService();
                        var launchDetails = connectionService.BuildLaunchDetails(profile, _sshDiagnosticsLevel);
                        
                        Session = SshSession.FromDefaultStore(
                            profile.Id,
                            cols,
                            rows,
                            _sshDiagnosticsLevel,
                            log: TerminalLogger.Log);
                        ShellCommand = Session.ShellCommand;
                        ShellArgs = string.Empty;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TerminalPane] SSH connection failed for '{profile.Name}': {ex.Message}");
                        Buffer.WriteContent($"\r\n[ERROR] SSH Connection Failed: {ex.Message}\r\n", false);
                        
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
                    skipPowerShellPostLaunchInit: _isShellIntegrationActive);
                Session.AttachBuffer(Buffer);

                TermView.SetSession(Session);
                Session.OnExit += code =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        LastExitCode = code;
                        ProcessExited?.Invoke(this, code);
                    });
                };
                UpdateCommandAssistContext();
            }
            catch (Exception ex)
            {
                // Graceful failure: Log and show in terminal
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Failed to spawn session: {ex.Message}");
                Buffer.WriteContent($"\r\n[ERROR] Failed to spawn process: {effectiveShell}\r\n", false);
                Buffer.WriteContent($"[DETAILS] {ex.Message}\r\n", false);
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

            // Wire up Resize
            TermView.OnResize += (c, r) =>
            {
                if (Parser != null)
                {
                    float cw = TermView.Metrics.CellWidth;
                    float ch = TermView.Metrics.CellHeight;
                    if (cw > 0) Parser.CellWidth = cw;
                    if (ch > 0) Parser.CellHeight = ch;
                }
                Session?.Resize(c, r);
            };

            // Metrics changed handling
            TermView.MetricsChanged += (cw, ch) =>
            {
                if (Parser != null && cw > 0 && ch > 0)
                {
                    Parser.CellWidth = cw;
                    Parser.CellHeight = ch;
                }
            };
        }



        public void ApplySettings(TerminalSettings settings)
        {
            _settings = settings;

            // Merge global settings with profile overrides
            // We create a "copy" for the view to use, but we only override specific visual fields
            var effectiveSettings = new TerminalSettings
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
                CommandAssistEnabled = settings.CommandAssistEnabled,
                CommandAssistHistoryEnabled = settings.CommandAssistHistoryEnabled,
                CommandAssistMaxHistoryEntries = settings.CommandAssistMaxHistoryEntries,
                CommandAssistAutoHideInAltScreen = settings.CommandAssistAutoHideInAltScreen,
                CommandAssistShellIntegrationEnabled = settings.CommandAssistShellIntegrationEnabled,
                CommandAssistPowerShellIntegrationEnabled = settings.CommandAssistPowerShellIntegrationEnabled,
                Profiles = settings.Profiles,
                DefaultProfileId = settings.DefaultProfileId
            };

            TermView.ApplySettings(effectiveSettings);
            InitializeCommandAssist();
            UpdateMinimumSizeConstraints();

            // Sync metrics to parser after settings change (font size, etc.)
            if (Parser != null)
            {
                float cw = TermView.Metrics.CellWidth;
                float ch = TermView.Metrics.CellHeight;
                if (cw > 0) Parser.CellWidth = cw;
                if (ch > 0) Parser.CellHeight = ch;
            }
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
            if (!IsCommandAssistFeatureEnabled())
            {
                return;
            }

            _commandAssistController?.ToggleAssist();
        }

        public bool OpenCommandAssistHelp()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            return _commandAssistController?.OpenHelp() ?? false;
        }

        public bool OpenCommandAssistHistorySearch()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            return _commandAssistController?.OpenHistorySearch() ?? false;
        }

        public void NotifyCommandAssistPaste(string text)
        {
            if (!IsCommandAssistFeatureEnabled())
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
            return _commandAssistController != null && !string.IsNullOrWhiteSpace(selectedText);
        }

        internal async Task<bool> ExplainSelectionAsync(string? selectedTextOverride = null)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            if (_commandAssistController == null)
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

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);
            UpdateFocusVisuals(true);
            TermView.InvalidateVisual();
        }

        protected override void OnLostFocus(RoutedEventArgs e)
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

            // Forward to PTY common handler
            // For now, we rely on Window forwarding, OR we implement it here.
            // PLAN: We will implement full OnKeyDown here in Phase 2.

            base.OnKeyDown(e);
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
            }, DispatcherPriority.Loaded);
        }

        public void Dispose()
        {
            if (Buffer != null)
            {
                Buffer.OnScreenSwitched -= OnBufferScreenSwitched;
            }
            _statusTimer?.Stop();
            _statusTimer = null;
            SftpService.Instance.JobUpdated -= Sftp_JobUpdated;
            Session?.Dispose();
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
                    SftpStatus.IsVisible = true;
                    SftpIcon.Text = activeJobs.Any(j => j.Direction == TransferDirection.Upload) ? "⬆" : "⬇";
                    SftpText.Text = $"SFTP: {activeJobs.Count} active transfers";
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
                        SftpIcon.Text = lastJob.State == TransferState.Completed ? "✅" : "❌";
                        SftpText.Text = lastJob.State == TransferState.Completed ? "SFTP complete" : $"SFTP failed: {lastJob.LastError}";
                    }
                    else
                    {
                        SftpStatus.IsVisible = false;
                    }
                }
            });
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

            string ext = format.ToLowerInvariant() switch {
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
                    string data = NovaTerminal.Core.Export.TerminalExporter.ExportToAnsi(Buffer);
                    using var stream = await file.OpenWriteAsync();
                    using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(data);
                }
                else
                {
                    string data = NovaTerminal.Core.Export.TerminalExporter.ExportToPlainText(Buffer);
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
            var registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();
            IShellIntegrationProvider? provider = registry.GetProvider(shellKind, profile);
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

            _ = _shellIntegrationEventDispatcher.EnqueueAsync(() => HandleShellIntegrationEventAsync(shellEvent));
        }

        internal async Task HandleCommandAssistCompletionAsync(int? exitCode)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return;
            }

            if (_commandAssistController == null)
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
            if (!IsCommandAssistFeatureEnabled() || _commandAssistController == null)
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
    }
}
