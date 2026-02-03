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

        private TerminalSettings? _settings;
        private bool _isUpdatingScroll = false;


        public Control ActiveControl => TermView;

        public TerminalPane()
        {
            InitializeComponent();
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(null, c, r);
            SetupCommon();
        }

        public TerminalPane(string shell)
        {
            InitializeComponent();
            Buffer = new TerminalBuffer(80, 24);
            TermView.SetBuffer(Buffer);
            TermView.Ready += (c, r) => InitializeSession(shell, c, r);
            SetupCommon();
        }

        private void SetupCommon()
        {
            // Wire up ScrollBar
            TermScrollBar.ValueChanged += ScrollBar_ValueChanged;

            // Search UI
            SetupSearch();

            // Load Settings
            ApplySettings(TerminalSettings.Load());
        }

        private void InitializeSession(string? shell, int cols, int rows)
        {
            if (Session != null || Buffer == null) return;

            if (cols <= 0 || rows <= 0) return;

            // Update buffer to match view exactly before starting PTY
            Buffer.Resize(cols, rows);
            Parser = new AnsiParser(Buffer);

            // Setup Session
            string effectiveShell = shell ?? ShellHelper.GetDefaultShell();
            ShellCommand = effectiveShell;

            Session = new RustPtySession(effectiveShell, cols, rows);
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
            TermView.ApplySettings(settings);
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
            SearchBox.TextChanged += (s, e) =>
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                    TermView.Search(SearchBox.Text);
            };

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

        public void ToggleSearch()
        {
            SearchPanel.IsVisible = !SearchPanel.IsVisible;
            if (SearchPanel.IsVisible)
            {
                SearchBox.Focus();
                if (!string.IsNullOrEmpty(SearchBox.Text))
                    TermView.Search(SearchBox.Text);
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
                // Detect if we are the only pane in our immediate container or parent tree
                // For MVP: Check if parent is a Grid with > 1 child.
                bool isAlone = true;
                if (Parent is Grid g && g.Children.Count > 1) isAlone = false;
                else if (Parent is ContentPresenter) isAlone = true; // Tab root

                FocusBorder.BorderBrush = (focused && !isAlone) ? Brushes.Cyan : Brushes.Transparent;
                FocusBorder.Opacity = (focused && !isAlone) ? 0.8 : 0.0;
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
                InitializeSession(ShellCommand, TermView.Cols, TermView.Rows);
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
