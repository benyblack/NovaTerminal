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
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Linq;
using Avalonia.Controls.Shapes;
using Avalonia.Automation;

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
        public event Action<TerminalPane, int>? ProcessExited;

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;
        private DispatcherTimer? _statusTimer;
        private bool _hasUserInteraction;

        public bool IsRecording => Session?.IsRecording ?? false;
        public string? CurrentWorkingDirectory { get; private set; }
        public string? CurrentOscTitle { get; private set; }
        public int? LastExitCode { get; private set; }
        public bool IsProcessRunning => Session?.IsProcessRunning ?? false;
        public bool HasUserInteraction => _hasUserInteraction;

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
        }

        public Control ActiveControl => TermView;

        public TerminalPane()
        {
            InitializeComponent();
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(null, null, c, r);
            SetupCommon();
        }

        public TerminalPane(string shell)
        {
            InitializeComponent();
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r);
            SetupCommon();
        }

        public TerminalPane(string shell, string args)
        {
            InitializeComponent();
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, null, c, r, args);
            SetupCommon();
        }

        public TerminalPane(TerminalProfile profile)
        {
            Profile = profile;
            InitializeComponent();
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(profile.Command, profile, c, r);
            SetupCommon();
        }

        private void SetupCommon()
        {
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
            TermView.MetricsChanged += (cw, ch) => UpdateMinimumSizeConstraints();

            // Load Settings
            ApplySettings(TerminalSettings.Load());
            UpdateMinimumSizeConstraints();
            AutomationProperties.SetName(TermView, "Terminal Pane");
            AutomationProperties.SetName(this, "Terminal Pane");

            // SFTP Context Menu
            var contextMenu = RootGrid.ContextMenu;
            if (contextMenu != null)
            {
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
            }



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
                Dispatcher.UIThread.Post(() =>
                {
                    CurrentWorkingDirectory = cwd;
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

            // Sync initial metrics
            float cw = TermView.Metrics.CellWidth;
            float ch = TermView.Metrics.CellHeight;
            if (cw > 0) Parser.CellWidth = cw;
            if (ch > 0) Parser.CellHeight = ch;

            // Setup Session
            string effectiveShell = shell ?? ShellHelper.GetDefaultShell();
            string args = explicitArgs ?? profile?.Arguments ?? "";

            // ADVANCED SSH: Generate correct argument chain for ssh.exe
            if (profile != null && profile.Type == ConnectionType.SSH)
            {
                // Ensure we have profiles for resolution. 
                var profiles = _settings?.Profiles ?? new System.Collections.Generic.List<TerminalProfile>();
                effectiveShell = "ssh.exe";
                effectiveShell = "ssh.exe";
                args = profile.GenerateSshArguments(profiles);
            }

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

                Session = new RustPtySession(effectiveShell, cols, rows, args, startingDir);

                TermView.SetSession(Session);
                Session.OnExit += code =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        LastExitCode = code;
                        ProcessExited?.Invoke(this, code);
                    });
                };
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
                Profiles = settings.Profiles,
                DefaultProfileId = settings.DefaultProfileId
            };

            TermView.ApplySettings(effectiveSettings);
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
                InactiveOverlay.IsVisible = !focused;
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
    }
}
