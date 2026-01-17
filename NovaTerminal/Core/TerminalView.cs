using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public class TerminalView : Control
    {
        public TerminalView()
        {
            Focusable = true;
            ClipToBounds = true;  // CRITICAL: Prevents SkiaSharp rendering from affecting other UI elements like tabs

            // Frame Limiter (60 FPS)
            _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnRenderTimerTick);
            _renderTimer.Start();
        }

        private TerminalBuffer? _buffer;

        // Coalescing
        private bool _isDirty;
        private DispatcherTimer _renderTimer;

        private void OnRenderTimerTick(object? sender, EventArgs e)
        {
            if (_isDirty)
            {
                _isDirty = false;
                InvalidateVisual();
            }
        }
        // A robust list attempting to find ANY font with Powerline/Nerd symbols.
        private const string FontFamilyList = "MesloLGM Nerd Font, MesloLGS NF, Cascadia Code, Cascadia Mono, Fira Code, JetBrains Mono, Consolas, Segoe UI, Segoe Fluent Icons, Segoe UI Symbol, Monospace";
        private Typeface _typeface = new Typeface(FontFamilyList, FontStyle.Normal, FontWeight.Normal);
        private double _fontSize = 14;
        private double _charWidth;
        private double _charHeight;
        private double _windowOpacity = 1.0;
        private bool _hasBackgroundImage = false;

        private IGlyphTypeface? _glyphTypeface;
        private SKTypeface? _skTypeface;
        private SKFont? _skFont;
        private double _baselineOffset;

        public double FontSize
        {
            get => _fontSize;
            set
            {
                _fontSize = value;
                ClearSkiaResources();
                InvalidateVisual();
            }
        }

        public Typeface Typeface
        {
            get => _typeface;
            set
            {
                _typeface = value;
                ClearSkiaResources();
                InvalidateVisual();
            }
        }

        public void ApplySettings(TerminalSettings settings)
        {
            // Check if font properties changed to avoid unnecessary Skia recreation (prevents crash on rapid opacity changes)
            bool fontChanged = Math.Abs(_fontSize - settings.FontSize) > 0.01 ||
                               (_typeface.FontFamily.Name != settings.FontFamily);

            _fontSize = settings.FontSize;
            if (fontChanged)
            {
                _typeface = new Typeface(settings.FontFamily);
            }
            _windowOpacity = settings.WindowOpacity;
            _hasBackgroundImage = !string.IsNullOrEmpty(settings.BackgroundImagePath) && System.IO.File.Exists(settings.BackgroundImagePath);

            if (_buffer != null)
            {
                _buffer.MaxHistory = settings.MaxHistory;

                // Store old theme for color remapping
                var oldTheme = _buffer.Theme;

                // Apply new theme
                if (settings.ThemeName == "Solarized Dark")
                    _buffer.Theme = TerminalTheme.SolarizedDark;
                else
                    _buffer.Theme = TerminalTheme.Dark;

                // Update all existing cells, remapping old theme colors to new
                _buffer.UpdateThemeColors(oldTheme);

                // Force immediate visual refresh
                InvalidateVisual();
            }

            // Only recreate resources if font changed
            if (fontChanged)
            {
                MeasureCharSize();

                // Trigger resize based on new font metrics and current bounds
                // BUT only if dimensions actually changed (to avoid overwriting theme-updated cells)
                if (_buffer != null && _charWidth > 0 && _charHeight > 0)
                {
                    int cols = (int)(Bounds.Width / _charWidth);
                    int rows = (int)(Bounds.Height / _charHeight);

                    if (cols > 0 && rows > 0 && (cols != _buffer.Cols || rows != _buffer.Rows))
                    {
                        _buffer.Resize(cols, rows);
                        OnResize?.Invoke(cols, rows);
                    }
                }
            }

            InvalidateVisual();
        }

        private void ClearSkiaResources()
        {
            _skFont?.Dispose();
            _skFont = null;
            _skTypeface?.Dispose();
            _skTypeface = null;
        }

        // Selection state
        private readonly SelectionState _selection = new SelectionState();
        private bool _isSelecting = false;
        private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 51, 153, 255));

        // Session for sending mouse events
        private ITerminalSession? _session;

        public void SetBuffer(TerminalBuffer buffer)
        {
            if (_buffer != null) _buffer.OnInvalidate -= InvalidateVisual;
            _buffer = buffer;
            if (_buffer != null) _buffer.OnInvalidate += InvalidateBuffer;

            MeasureCharSize();
            InvalidateVisual();
        }

        private void MeasureCharSize()
        {
            // Measure char size and get GlyphTypeface
            var testText = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
            _charWidth = testText.Width;
            _charHeight = testText.Height;
            _baselineOffset = testText.Baseline;

            // Try to get IGlyphTypeface for low-level rendering
            _glyphTypeface = _typeface.GlyphTypeface;

            // OPTIMIZATION: Cache Skia Typeface & Font to avoid per-frame lookup/alloc
            ClearSkiaResources();
            try
            {
                _skTypeface = SkiaSharp.SKTypeface.FromFamilyName(_typeface.FontFamily.Name);
                if (_skTypeface != null)
                {
                    _skFont?.Dispose();
                    _skFont = new SKFont(_skTypeface, (float)_fontSize);
                }
            }
            catch { }
        }

        public void SetSession(ITerminalSession session)
        {
            _session = session;
        }

        public event Action<int, int>? ScrollStateChanged;
        private int _scrollOffset = 0;
        private DispatcherTimer? _autoScrollTimer;
        private int _autoScrollDirection = 0; // -1 up, 1 down

        public int ScrollOffset
        {
            get => _scrollOffset;
            set
            {
                if (_buffer == null) return;
                int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
                _scrollOffset = Math.Clamp(value, 0, maxScroll);
                InvalidateBuffer();
            }
        }

        public void SetScrollOffset(int offset)
        {
            ScrollOffset = offset;
        }

        private bool _isInvalidationPending = false;

        // Search state
        private List<SearchMatch> _searchMatches = new List<SearchMatch>();
        private int _activeSearchIndex = -1;

        public event Action<int, int>? SearchStateChanged;

        public void Search(string query)
        {
            if (_buffer == null) return;
            _searchMatches = _buffer.FindMatches(query);
            _activeSearchIndex = _searchMatches.Count > 0 ? 0 : -1;

            if (_activeSearchIndex != -1)
            {
                ScrollToMatch(_searchMatches[_activeSearchIndex]);
            }

            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        public void NextMatch()
        {
            if (_searchMatches.Count == 0) return;
            _activeSearchIndex = (_activeSearchIndex + 1) % _searchMatches.Count;
            ScrollToMatch(_searchMatches[_activeSearchIndex]);
            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        public void PrevMatch()
        {
            if (_searchMatches.Count == 0) return;
            _activeSearchIndex = (_activeSearchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
            ScrollToMatch(_searchMatches[_activeSearchIndex]);
            SearchStateChanged?.Invoke(_activeSearchIndex + 1, _searchMatches.Count);
            InvalidateVisual();
        }

        public void ClearSearch()
        {
            _searchMatches.Clear();
            _activeSearchIndex = -1;
            SearchStateChanged?.Invoke(0, 0);
            InvalidateVisual();
        }

        private void ScrollToMatch(SearchMatch match)
        {
            if (_buffer == null) return;

            int totalLines = _buffer.TotalLines;
            int viewportRows = _buffer.Rows;

            // match.AbsRow is 0-indexed from top of scrollback
            // Current viewport shows [totalLines - viewportRows - _scrollOffset, totalLines - _scrollOffset]

            int viewTop = totalLines - viewportRows - _scrollOffset;
            int viewBottom = totalLines - _scrollOffset;

            if (match.AbsRow < viewTop || match.AbsRow >= viewBottom)
            {
                // Put match in the middle if possible
                int newScrollOffset = totalLines - match.AbsRow - (viewportRows / 2);
                ScrollOffset = Math.Max(0, Math.Min(newScrollOffset, totalLines - viewportRows));
            }
        }

        public void InvalidateBuffer()
        {
            _isDirty = true;
        }

        public event Action? OnReady;
        public event Action<int, int>? OnResize;
        private bool _isReady;

        private void StartAutoScroll(int direction)
        {
            _autoScrollDirection = direction;
            if (_autoScrollTimer == null)
            {
                _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Render);
                _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }
            if (!_autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Start();
            }
        }

        private void StopAutoScroll()
        {
            if (_autoScrollTimer != null && _autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Stop();
            }
        }

        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (_buffer == null || _autoScrollDirection == 0) return;

            // Adjust scroll offset
            int newOffset = ScrollOffset - _autoScrollDirection; // Offset decreases when scrolling down (towards 0/end)
                                                                 // Wait, ScrollOffset 0 is bottom (end). Higher values go back in history.
                                                                 // If dragging DOWN (direction=1), we want to see newer lines -> Decrease Offset.
                                                                 // If dragging UP (direction=-1), we want to see older lines -> Increase Offset.

            // Re-clamping logic:
            int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
            newOffset = Math.Clamp(newOffset, 0, maxScroll);

            if (newOffset != ScrollOffset)
            {
                ScrollOffset = newOffset;

                // Update selection to current mouse position relative to NEW scroll
                try
                {
                    // Accessing pointer position is tricky inside timer without event args.
                    // We can rely on the fact that OnPointerMoved updates _selection.End 
                    // BUT OnPointerMoved fires on mouse move. If mouse is still, we need to update selection end based on new scroll.
                    // Actually, simpler: The selection end is an absolute row. 
                    // If we scroll, the mouse is now over a DIFFERENT absolute row.

                    // We should track last known mouse position or just let the user move mouse.
                    // But standard behavior is: hold mouse at bottom -> scroll -> selection expands.
                    // To do this, we need to update _selection.End to the row currently at the bottom (or top) visual edge.

                    int targetVisualRow = (_autoScrollDirection > 0) ? _buffer.Rows - 1 : 0;

                    // Convert visual row to absolute row with NEW offset
                    int totalLines = _buffer.TotalLines;
                    int displayStart = Math.Max(0, totalLines - _buffer.Rows - ScrollOffset);
                    int absRow = displayStart + targetVisualRow;

                    // We need to keep the Column from the initial selection/drag. 
                    // But for full line selection feeling, usually it goes to end/start of line.
                    // Let's just update the Row, keep Col from existing selection end? No, that might be weird.
                    // Ideally we'd poll mouse position, but complex in Avalonia without reference.
                    // Let's assume extending to the full width of the new row is acceptable for vertical drag,
                    // or just keep the previous column.

                    _selection.End = (absRow, _selection.End.Col);
                    InvalidateVisual();
                }
                catch { }
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_buffer != null && _charWidth > 0 && _charHeight > 0)
            {
                // Padding must match TerminalDrawOperation (PaddingLeft = 4)
                // We subtract padding from available width to avoid clipping last column
                int availableWidth = Math.Max(0, (int)e.NewSize.Width - 4);

                int cols = (int)(availableWidth / _charWidth);
                int rows = (int)(e.NewSize.Height / _charHeight);

                Console.WriteLine($"[TerminalView] OnSizeChanged: {e.NewSize.Width}x{e.NewSize.Height} -> {cols}cols x {rows}rows");

                if (cols > 0 && rows > 0)
                {
                    // No snapshot - rely on Edge Extension + Sync Resize
                    _buffer.Resize(cols, rows);

                    if (!_isReady)
                    {
                        // Ensure we don't start with a tiny transient layout (e.g. before Window is fully sized)
                        // 40 cols and 10 rows is a reasonable minimum for a functional terminal.
                        if (cols >= 40 && rows >= 10)
                        {
                            _isReady = true;
                            OnReady?.Invoke();
                            Console.WriteLine("[TerminalView] Ready! Initializing Session.");
                        }
                    }
                    else
                    {
                        // Fire resize event for already-started sessions
                        OnResize?.Invoke(cols, rows);
                        InvalidateBuffer();
                    }
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            if (_buffer == null || _glyphTypeface == null)
            {
                return;
            }

            // Create and dispatch custom draw op
            var drawOp = new TerminalDrawOperation(
                new Rect(0, 0, Bounds.Width, Bounds.Height),
                _buffer,
                _scrollOffset,
                _selection,
                _searchMatches,
                _activeSearchIndex,
                _charWidth,
                _charHeight,
                _baselineOffset,
                _typeface,
                _fontSize,
                _glyphTypeface,
                _skTypeface,
                _skFont,
                _windowOpacity,
                _hasBackgroundImage
            );

            context.Custom(drawOp);
        }

        // Mouse event handlers
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_buffer == null) return;

            // Scroll up (positive) -> Increase Offset
            // Scroll down (negative) -> Decrease Offset
            int delta = (int)(e.Delta.Y * 3); // 3 lines per notch

            // If scrolling UP (Delta > 0), we want to see History. History is at Offset > 0.
            // So UP wheel means Increase Offset.

            ScrollOffset += delta;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);

            if (point.Properties.IsLeftButtonPressed)
            {
                // Check if application has enabled mouse reporting
                if (_buffer != null && _buffer.IsMouseReportingActive())
                {
                    // Forward mouse event to shell
                    SendMouseEvent(e, pressed: true);
                    e.Handled = true;
                    return;
                }

                // Normal mode: Handle selection
                var (row, col) = ScreenToTerminal(point.Position);

                // Check for double/triple-click
                if (e.ClickCount == 2)
                {
                    // Double-click: Select word
                    SelectWord(row, col);
                    _isSelecting = false; // Don't start drag selection
                }
                else if (e.ClickCount >= 3)
                {
                    // Triple-click: Select line
                    SelectLine(row);
                    _isSelecting = false; // Don't start drag selection
                }
                else
                {
                    // Single click: Start selection
                    _selection.Start = (row, col);
                    _selection.End = (row, col);
                    _selection.IsActive = true;
                    _isSelecting = true;
                }

                InvalidateVisual();
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            // Forward motion events if mouse reporting is active
            if (_buffer != null && _buffer.IsMouseReportingActive())
            {
                var point = e.GetCurrentPoint(this);

                // Only send motion when a button is actually pressed (drag)
                // Mode 1003 should track motion during button press, not on hover
                bool anyButtonPressed = point.Properties.IsLeftButtonPressed ||
                                       point.Properties.IsMiddleButtonPressed ||
                                       point.Properties.IsRightButtonPressed;

                if (anyButtonPressed)
                {
                    SendMouseEvent(e, pressed: true, motion: true);
                }

                e.Handled = true;
                return;
            }

            if (_isSelecting)
            {
                var point = e.GetCurrentPoint(this);
                var (absRow, col) = ScreenToTerminal(point.Position);

                // Update selection end
                _selection.End = (absRow, col);

                // Auto-scroll detection
                double zoneSize = _charHeight * 2; // Drag within top/bottom 2 lines
                if (point.Position.Y < zoneSize)
                {
                    // Near Top -> Scroll Up (Increase Offset)
                    StartAutoScroll(-1);
                }
                else if (point.Position.Y > Bounds.Height - zoneSize)
                {
                    // Near Bottom -> Scroll Down (Decrease Offset)
                    StartAutoScroll(1);
                }
                else
                {
                    StopAutoScroll();
                }

                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            // Forward release events if mouse reporting is active
            if (_buffer != null && _buffer.IsMouseReportingActive())
            {
                SendMouseEvent(e, pressed: false);
                e.Handled = true;
                return;
            }

            if (_isSelecting)
            {
                _isSelecting = false;
                StopAutoScroll();

                // If start == end, clear selection (was just a click)
                if (_selection.Start == _selection.End)
                {
                    _selection.Clear();
                    InvalidateVisual();
                }
            }
        }

        /// <summary>
        /// Sends a mouse event to the shell in SGR format when mouse reporting is enabled.
        /// </summary>
        private void SendMouseEvent(PointerEventArgs e, bool pressed, bool motion = false)
        {
            if (_session == null || _buffer == null) return;

            var point = e.GetCurrentPoint(this);
            var (row, col) = ScreenToTerminal(point.Position);

            // Convert to 1-indexed coordinates
            int x = col + 1;
            int y = row + 1;

            // Determine button
            int button = 0;
            if (point.Properties.IsLeftButtonPressed) button = 0;
            else if (point.Properties.IsMiddleButtonPressed) button = 1;
            else if (point.Properties.IsRightButtonPressed) button = 2;

            // Add motion flag if this is a motion event and AnyEvent mode is active
            if (motion && _buffer.MouseModeAnyEvent)
            {
                button += 32; // Motion indicator
            }

            // Only send if we have SGR mode enabled
            if (_buffer.MouseModeSGR)
            {
                // SGR format: CSI < button ; x ; y M/m
                char finalChar = pressed ? 'M' : 'm';
                string sequence = $"\x1b[<{button};{x};{y}{finalChar}";

                _session.SendInput(sequence);
            }
            else if (_buffer.IsMouseReportingActive())
            {
                // X10/Legacy format: CSI M bxy (fallback for WSL)
                // Only send press events in X10 mode (no release)
                if (pressed)
                {
                    // X10 format: button and coordinates are sent as single bytes offset by 32
                    char buttonChar = (char)(32 + button);
                    char xChar = (char)(32 + x);
                    char yChar = (char)(32 + y);

                    // Clamp to valid range (coordinates must be < 223)
                    if (x < 223 && y < 223)
                    {
                        string sequence = $"\x1b[M{buttonChar}{xChar}{yChar}";
                        _session.SendInput(sequence);
                    }
                }
            }
        }

        /// <summary>
        /// Copies selected text to clipboard.
        /// </summary>
        public async Task<bool> CopySelectionToClipboard()
        {
            if (!_selection.IsActive || _buffer == null)
                return false;

            try
            {
                var text = _selection.GetSelectedText(_buffer);
                if (!string.IsNullOrEmpty(text))
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel?.Clipboard != null)
                    {
                        await topLevel.Clipboard.SetTextAsync(text);
                        return true;
                    }
                }
            }
            catch
            {
                // Clipboard operations can fail
            }

            return false;
        }

        /// <summary>
        /// Returns selected text without copying to clipboard.
        /// </summary>
        public string? GetSelectedText()
        {
            if (!_selection.IsActive || _buffer == null)
                return null;

            return _selection.GetSelectedText(_buffer);
        }

        /// <summary>
        /// Clears the current selection.
        /// </summary>
        public void ClearSelection()
        {
            _selection.Clear();
            InvalidateVisual();
        }

        /// <summary>
        /// Checks if there's an active selection.
        /// </summary>
        public bool HasSelection() => _selection.IsActive;

        /// <summary>
        /// Selects a word at the given position (double-click behavior).
        /// </summary>
        private void SelectWord(int row, int col)
        {
            if (_buffer == null) return;

            int startCol = col;
            int endCol = col;

            // ScreenToTerminal now returns absolute rows.
            // But SelectWord is usually called from visual clicks.
            // ScreenToTerminal handles the conversion, so 'row' passed here IS absolute row.
            // GetCellAbsolute is needed.

            // Find word boundaries (non-whitespace characters)
            while (startCol > 0 && !IsWhitespace(_buffer.GetCellAbsolute(startCol - 1, row).Character))
                startCol--;

            while (endCol < _buffer.Cols - 1 && !IsWhitespace(_buffer.GetCellAbsolute(endCol + 1, row).Character))
                endCol++;

            _selection.Start = (row, startCol);
            _selection.End = (row, endCol);
            _selection.IsActive = true;
        }

        /// <summary>
        /// Selects an entire line (triple-click behavior).
        /// </summary>
        private void SelectLine(int row)
        {
            if (_buffer == null) return;

            _selection.Start = (row, 0);
            _selection.End = (row, _buffer.Cols - 1);
            _selection.IsActive = true;
        }

        /// <summary>
        /// Checks if a character is whitespace.
        /// </summary>
        private static bool IsWhitespace(char c)
        {
            return char.IsWhiteSpace(c) || c == '\0';
        }

        /// <summary>
        /// Converts screen coordinates to terminal ABSOLUTE row/col.
        /// </summary>
        private (int Row, int Col) ScreenToTerminal(Point position)
        {
            if (_buffer == null) return (0, 0);

            int col = (int)(position.X / _charWidth);
            int visualRow = (int)(position.Y / _charHeight);

            // Clamp visual row first
            visualRow = Math.Clamp(visualRow, 0, _buffer.Rows - 1);

            // Convert to Absolute Row
            // Visible Top Index = Total - Rows - Offset
            int totalLines = _buffer.TotalLines;
            int displayStart = Math.Max(0, totalLines - _buffer.Rows - _scrollOffset);
            int absRow = displayStart + visualRow;

            // Clamp columns
            col = Math.Clamp(col, 0, _buffer.Cols - 1);

            // AbsRow shouldn't need clamping if logic correct, but safety:
            absRow = Math.Clamp(absRow, 0, totalLines - 1);

            return (absRow, col);
        }
    }
}
