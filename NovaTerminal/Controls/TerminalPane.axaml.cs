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

namespace NovaTerminal.Controls
{
    public partial class TerminalPane : UserControl, IDisposable
    {
        public ITerminalSession? Session { get; private set; }
        public TerminalBuffer? Buffer { get; private set; }
        public AnsiParser? Parser { get; private set; }
        public string ShellCommand { get; private set; } = string.Empty;
        public string ShellArgs { get; private set; } = string.Empty;
        public TerminalProfile? Profile { get; private set; }

        public event Action<TerminalPane, TransferDirection, TransferKind>? RequestSftpTransfer;

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;
        private DispatcherTimer? _statusTimer;

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

            // Load Settings
            ApplySettings(TerminalSettings.Load());

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
            }



        }

        private void InitializeSession(string? shell, TerminalProfile? profile, int cols, int rows, string? explicitArgs = null)
        {
            if (Session != null || Buffer == null) return;

            if (cols <= 0 || rows <= 0) return;

            // Update buffer to match view exactly before starting PTY
            Buffer.Resize(cols, rows);
            Parser = new AnsiParser(Buffer);

            // Sync initial metrics
            float cw = TermView.Metrics.CellWidth;
            float ch = TermView.Metrics.CellHeight;
            if (cw > 0) Parser.CellWidth = cw;
            if (ch > 0) Parser.CellHeight = ch;
            TerminalLogger.Log($"[TERMINAL_PANE] Parser initialized: CellWidth={Parser.CellWidth} (view={cw}), CellHeight={Parser.CellHeight} (view={ch}), Cols={cols}, Rows={rows}");

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

                // Fetch password from Vault for SSH
                if (profile != null && profile.Type == ConnectionType.SSH)
                {
                    var vault = new VaultService();
                    string? pwd = vault.GetSecret($"profile_{profile.Id}_password");
                    if (!string.IsNullOrEmpty(pwd))
                    {
                        Session.SetSavedPassword(pwd);
                    }
                }

                TermView.SetSession(Session);
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
                Dispatcher.UIThread.Post(UpdateScrollUI);
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
                    TerminalLogger.Log($"[TERMINAL_PANE] OnResize sync: CellWidth={Parser.CellWidth} (view={cw}), CellHeight={Parser.CellHeight} (view={ch})");
                }
                Session?.Resize(c, r);
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

                // Inherit everything else from global
                MaxHistory = settings.MaxHistory,
                WindowOpacity = settings.WindowOpacity,
                BlurEffect = settings.BlurEffect,
                BackgroundImagePath = settings.BackgroundImagePath,
                BackgroundImageOpacity = settings.BackgroundImageOpacity,
                BackgroundImageStretch = settings.BackgroundImageStretch,
                Profiles = settings.Profiles,
                DefaultProfileId = settings.DefaultProfileId
            };

            TermView.ApplySettings(effectiveSettings);

            // Sync metrics to parser after settings change (font size, etc.)
            if (Parser != null)
            {
                float cw = TermView.Metrics.CellWidth;
                float ch = TermView.Metrics.CellHeight;
                if (cw > 0) Parser.CellWidth = cw;
                if (ch > 0) Parser.CellHeight = ch;
                TerminalLogger.Log($"[TERMINAL_PANE] ApplySettings sync: CellWidth={Parser.CellWidth} (view={cw}), CellHeight={Parser.CellHeight} (view={ch})");
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
                TerminalLogger.Log($"[TERMINAL_PANE] UpdateScrollUI: JustSwitchedFromAltScreen = {TermView.JustSwitchedFromAltScreen}, Buffer TotalLines = {Buffer?.TotalLines}, Buffer Rows = {Buffer?.Rows}");

                if (TermView.JustSwitchedFromAltScreen)
                {
                    TerminalLogger.Log($"[TERMINAL_PANE] UpdateScrollUI: Handling post-alt-screen-switch case");
                    // Small delay to ensure screen switch processing is complete
                    await Task.Delay(10);
                    // Ensure cursor is visible after screen switch
                    TermView.EnsureCursorVisible();
                    // Note: EnsureCursorVisible() handles resetting the flag internally
                }
                else
                {
                    TerminalLogger.Log($"[TERMINAL_PANE] UpdateScrollUI: Handling normal output case");
                    // For normal output, ensure the cursor is visible
                    TermView.EnsureCursorVisible();
                }

                TerminalLogger.Log($"[TERMINAL_PANE] UpdateScrollUI: Final ScrollOffset = {TermView.ScrollOffset}");
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
            if (FocusBorder != null)
            {
                // Disable the old border visual
                FocusBorder.IsVisible = false;
            }

            // Option 1: Inactive Dimming - DISABLED per user feedback
            // TermView.Opacity = focused ? 1.0 : 0.5;
            TermView.Opacity = 1.0;

            // Re-render to ensure cursor state updates
            TermView.InvalidateVisual();
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
