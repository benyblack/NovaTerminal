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

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;

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

            // Search UI
            SetupSearch();

            // Wire up focus syncing
            TermView.GotFocus += (s, e) => UpdateFocusVisuals(true);
            TermView.LostFocus += (s, e) => UpdateFocusVisuals(false);

            // Load Settings
            ApplySettings(TerminalSettings.Load());
        }

        private void InitializeSession(string? shell, TerminalProfile? profile, int cols, int rows, string? explicitArgs = null)
        {
            if (Session != null || Buffer == null) return;

            if (cols <= 0 || rows <= 0) return;

            // Update buffer to match view exactly before starting PTY
            Buffer.Resize(cols, rows);
            Parser = new AnsiParser(Buffer);

            // Setup Session
            string effectiveShell = shell ?? ShellHelper.GetDefaultShell();
            string args = explicitArgs ?? profile?.Arguments ?? "";

            // ADVANCED SSH: Generate correct argument chain for ssh.exe
            if (profile != null && profile.Type == ConnectionType.SSH)
            {
                // Ensure we have profiles for resolution. 
                var profiles = _settings?.Profiles ?? new System.Collections.Generic.List<TerminalProfile>();
                effectiveShell = "ssh.exe";
                args = profile.GenerateSshArguments(profiles);
            }

            ShellCommand = effectiveShell;
            ShellArgs = args;
            string startingDir = profile?.StartingDirectory ?? "";

            Session = new RustPtySession(effectiveShell, cols, rows, args, startingDir);

            // Fetch password from Vault for automated injection if it's an SSH connection
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

            // Wire up Output
            Session.OnOutputReceived += text =>
            {
                Parser.Process(text);
                Dispatcher.UIThread.Post(UpdateScrollUI);
            };

            // Wire up Resize
            TermView.OnResize += (c, r) => Session?.Resize(c, r);
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
            // Propagate theme to ScrollBar/Search if needed
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
            Session?.Dispose();
        }
    }
}
