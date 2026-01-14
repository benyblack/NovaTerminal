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

namespace NovaTerminal.Core
{
    public class TerminalView : Control
    {
        public TerminalView()
        {
            Focusable = true;
        }

        private TerminalBuffer? _buffer;
        // A robust list attempting to find ANY font with Powerline/Nerd symbols.
        private const string FontFamilyList = "MesloLGM Nerd Font, MesloLGS NF, Cascadia Code, Cascadia Mono, Fira Code, JetBrains Mono, Consolas, Segoe UI, Segoe Fluent Icons, Segoe UI Symbol, Monospace";
        private Typeface _typeface = new Typeface(FontFamilyList, FontStyle.Normal, FontWeight.Normal);
        private double _fontSize = 14;
        private double _charWidth;
        private double _charHeight;

        private IGlyphTypeface? _glyphTypeface;
        private double _baselineOffset;

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
            
            // Measure char size and get GlyphTypeface
            var testText = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
            _charWidth = testText.Width;
            _charHeight = testText.Height;
            _baselineOffset = testText.Baseline;

            // Try to get IGlyphTypeface for low-level rendering
            _glyphTypeface = _typeface.GlyphTypeface;
            
            InvalidateVisual();
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

        public void InvalidateBuffer()
        {
            // Try synchronous first to see if async dispatch is causing timing issues
            if (Dispatcher.UIThread.CheckAccess())
            {
                InvalidateVisual();
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Render);
            }
            
            // Notify scroll change if buffer size changed (e.g. new lines added)
            if (_buffer != null)
            {
                // We dispatch this too to ensure UI thread handles the event (since it might update ScrollBar)
                Dispatcher.UIThread.Post(() => 
                {
                    if (_buffer != null) 
                        ScrollStateChanged?.Invoke(_scrollOffset, _buffer.TotalLines);
                });
            }
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
                int cols = (int)(e.NewSize.Width / _charWidth);
                int rows = (int)(e.NewSize.Height / _charHeight);

                if (cols > 0 && rows > 0)
                {
                    _buffer.Resize(cols, rows);
                    
                    // Log for debugging
                    try { System.IO.File.AppendAllText("d:/projects/nova2/NovaTerminal/startup_debug.txt", $"[View Resize] {e.NewSize.Width}x{e.NewSize.Height} -> {cols} cols, {rows} rows\n"); } catch {}

                    if (!_isReady)
                    {
                        // Ensure we don't start with a tiny transient layout (e.g. before Window is fully sized)
                        // 40 cols is a reasonable minimum for a functional terminal.
                        if (cols >= 40)
                        {
                            _isReady = true;
                            OnReady?.Invoke();
                            try { System.IO.File.AppendAllText("d:/projects/nova2/NovaTerminal/startup_debug.txt", $"[OnReady Fired] {cols}x{rows}\n"); } catch {}
                        }
                    }
                    else
                    {
                        // Fire resize event for already-started sessions
                        OnResize?.Invoke(cols, rows);
                    }
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (_buffer == null) return;

            // Clear background
            context.FillRectangle(Brushes.Black, this.Bounds);

            if (_glyphTypeface == null) return; // Fallback? Or just wait for load?

            // Batching Lists
            
            // Iterate rows
            int totalLines = _buffer.TotalLines;
            int displayStart = Math.Max(0, totalLines - _buffer.Rows - _scrollOffset);

            for (int r = 0; r < _buffer.Rows; r++)
            {
                double y = r * _charHeight;
                double baselineY = y + _baselineOffset;

                // Pass 1: Backgrounds (Batched)
                for (int c = 0; c < _buffer.Cols; c++)
                {
                    var cell = _buffer.GetCell(c, r, _scrollOffset);
                    if (cell.Background != Colors.Black)
                    {
                        var bg = cell.Background;
                        int runStart = c;
                        
                        // Find run length
                        int k = c + 1;
                        while(k < _buffer.Cols)
                        {
                            if (_buffer.GetCell(k, r, _scrollOffset).Background != bg) break;
                            k++;
                        }
                        
                        // Draw single rect for the whole run
                        int runLength = k - c;
                        
                         // To avoid sub-pixel gaps between adjacent runs, we can round-up or add slight overlap.
                         // But simply drawing one big rect solves the internal gaps.
                        var bgBrush = new SolidColorBrush(bg);
                        context.FillRectangle(bgBrush, new Rect(runStart * _charWidth, y, runLength * _charWidth, _charHeight));
                        
                        c = k - 1;
                    }
                }

                // Pass 1.5: Selection Highlight (if any)
                // Pass 1.5: Selection Highlight (if any)
                if (_selection.IsActive)
                {
                    // Calculate absolute row for visual row r
                    int absRow = displayStart + r;
                    var (isSelected, colStart, colEnd) = _selection.GetSelectionRangeForRow(absRow, _buffer.Cols);
                    
                    if (isSelected)
                    {
                        var selRect = new Rect(
                            colStart * _charWidth,
                            y,
                            (colEnd - colStart + 1) * _charWidth,
                            _charHeight
                        );
                        context.FillRectangle(SelectionBrush, selRect);
                    }
                }

                // Pass 2: Foregrounds (Batched)
                for (int c = 0; c < _buffer.Cols; c++)
                {
                    var cell = _buffer.GetCell(c, r, _scrollOffset);
                    
                    if (cell.Character != ' ' && cell.Character != '\0')
                    {
                        Color fg = cell.Foreground;
                        
                        // Look ahead to find length of run with same FG
                        int runStart = c;
                        
                        // Initialize run
                        var glyphInfos = new List<GlyphInfo>();
                        
                        // Add first char
                        ushort glyph = _glyphTypeface.GetGlyph(cell.Character);
                        
                        if (glyph == 0)
                        {
                            // Fallback using FormattedText
                            var ft = new FormattedText(
                                cell.Character.ToString(),
                                CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                _typeface, 
                                _fontSize,
                                new SolidColorBrush(fg)
                            );
                            context.DrawText(ft, new Point(c * _charWidth, y));
                        }
                        else
                        {
                            // Normal glyph found
                            glyphInfos.Add(new GlyphInfo(glyph, 0, _charWidth));
                            
                            // Continue run
                            int k = c + 1;
                            while(k < _buffer.Cols)
                            {
                                var nextCell = _buffer.GetCell(k, r, _scrollOffset);
                                if (nextCell.Foreground != fg) break;
                                
                                var nextChar = nextCell.Character;
                                if (nextChar == ' ' || nextChar == '\0') 
                                {
                                    // Spaces break the visual run of *glyphs* usually? 
                                    // Actually, we can just skip drawing spaces but keep the run if color is same.
                                    // But simpler to just treat space as a glyph if the font has it, or break.
                                    // Ideally spaces are just empty. 
                                    // Let's break for safety or handle space glyph.
                                    // For now, simple logic: Break.
                                    break; 
                                }

                                var nextGlyph = _glyphTypeface.GetGlyph(nextChar);
                                
                                if (nextGlyph == 0) 
                                {
                                    break;
                                }
                                
                                glyphInfos.Add(new GlyphInfo(nextGlyph, k - c, _charWidth));
                                k++;
                            }

                            // Flush Run
                            if (glyphInfos.Count > 0)
                            {
                                var origin = new Point(runStart * _charWidth, baselineY);
                                var glyphRun = new GlyphRun(
                                    _glyphTypeface,
                                    _fontSize,
                                    characters: default, 
                                    glyphInfos: glyphInfos,
                                    baselineOrigin: origin
                                );
                                context.DrawGlyphRun(new SolidColorBrush(fg), glyphRun);
                            }
                            
                            // Advance c
                            c = k - 1;
                        }
                    }
                }
            }

            // Draw Cursor
            double cursorX = _buffer.CursorCol * _charWidth;
            int visualCursorRow = _buffer.GetVisualCursorRow(_scrollOffset);
            
            if (visualCursorRow >= 0 && visualCursorRow < _buffer.Rows)
            {
                double cursorY = visualCursorRow * _charHeight;
                context.FillRectangle(Brushes.White, new Rect(cursorX, cursorY + _charHeight - 2, _charWidth, 2)); 
            }
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
