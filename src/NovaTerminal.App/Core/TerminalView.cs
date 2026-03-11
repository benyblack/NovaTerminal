using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;

namespace NovaTerminal.Core
{
    public struct CellMetrics
    {
        public float CellWidth;
        public float CellHeight;
        public float Baseline;
        public float Ascent;
        public float Descent;
        public float Leading;
    }

    public readonly record struct CommandAssistPromptHint(
        int VisibleCursorVisualRow,
        int VisibleRows,
        float CellWidth,
        float CellHeight);

    public class TerminalView : Control
    {
        private readonly RowImageCache _rowCache = new();
        public CellMetrics Metrics => _metrics;

        /// <summary>
        /// Fired when font metrics (cell width/height) change.
        /// </summary>
        public event Action<float, float>? MetricsChanged;
        public event Action? CommandAssistAnchorHintChanged;

        private bool _showRenderHud;
        public bool ShowRenderHud
        {
            get => _showRenderHud;
            set
            {
                if (_showRenderHud != value)
                {
                    _showRenderHud = value;
                    _isDirty = true;
                    if (_isUiRenderable) InvalidateVisual();
                }
            }
        }

        public TerminalView()
        {
            Focusable = true;
            ClipToBounds = true;

            // Allow receiving focus via click
            PointerPressed += (s, e) => Focus();

            _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnRenderTimerTick);

            _cursorBlinkTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Render, OnCursorBlinkTick);
            _scrollAnimationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnScrollAnimationTick);
            _metricsTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, OnMetricsTimerTick);
            _metricsTimer.Start();

            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, async (s, e) => {
                try {
                    await OnDropAsync(s, e);
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine($"[TerminalView] OnDropAsync Failed: {ex}");
                }
            });
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (!e.Handled && _session != null && !string.IsNullOrEmpty(e.Text))
            {
                ResetCursorBlink();
                _session.SendInput(e.Text);
                TextInputObserved?.Invoke(e.Text);
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            if (HandleKeyDownCore(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
            }
        }

        internal Func<Key, KeyModifiers, bool>? KeyDownInterceptor { get; set; }

        internal bool HandleKeyDownCore(Key key, KeyModifiers keyModifiers)
        {
            if (KeyDownInterceptor?.Invoke(key, keyModifiers) == true)
            {
                return true;
            }

            if (_session == null)
            {
                return false;
            }

            ResetCursorBlink();

            // Handle keys that don't generate text input (Control codes, arrows, etc.)
            // Logic copied from MainWindow
            string? sequence = null;
            bool isCtrl = (keyModifiers & KeyModifiers.Control) != 0;

            switch (key)
            {
                case Key.Enter:
                    _session.SendInput("\r");
                    EnterObserved?.Invoke();
                    return true;
                case Key.Back:
                    _session.SendInput("\x7f");
                    BackspaceObserved?.Invoke();
                    return true;
                case Key.Tab:
                    _session.SendInput("\t");
                    return true;
                case Key.Escape:
                    _session.SendInput("\x1b");
                    return true;

                default:
                    if (isCtrl && !keyModifiers.HasFlag(KeyModifiers.Shift) && !keyModifiers.HasFlag(KeyModifiers.Alt))
                    {
                        if (key >= Key.A && key <= Key.Z)
                        {
                            // Ctrl+A = 1, Ctrl+Z = 26
                            // ASCII Control Characters
                            char ctrlChar = (char)(key - Key.A + 1);
                            _session.SendInput(ctrlChar.ToString());
                            return true;
                        }
                    }
                    break;

                case Key.C:
                    if (isCtrl)
                    {
                        if (HasSelection())
                        {
                            _ = CopySelectionToClipboard();
                            ClearSelection();
                        }
                        else
                        {
                            _session.SendInput("\x03");
                        }
                        return true;
                    }
                    break;
                case Key.V:
                    if (isCtrl)
                    {
                        // Clipboard paste - handled by wrapping logic or we need dependency injection?
                        // TerminalView technically doesn't know about Window's PasteFromClipboard.
                        // But providing a way to paste is essential.
                        // For now we might leave CTRL+V in MainWindow or raise an event?
                        // MainWindow is better for accessing Clipboard safely.
                        // So we WON'T handle Ctrl+V here, let it bubble/tunnel?
                        // Actually MainWindow has a global handler for Ctrl+V.
                        // If we don't handle it here, MainWindow will see it?
                        // MainWindow's handler is Tunnel, so it sees it BEFORE this.
                        // So Ctrl+V is fine in MainWindow.
                    }
                    break;

                // Arrows
                case Key.Up:
                    sequence = _buffer != null && _buffer.Modes.IsApplicationCursorKeys ? "\x1bOA" : "\x1b[A";
                    break;
                case Key.Down:
                    sequence = _buffer != null && _buffer.Modes.IsApplicationCursorKeys ? "\x1bOB" : "\x1b[B";
                    break;
                case Key.Right:
                    sequence = _buffer != null && _buffer.Modes.IsApplicationCursorKeys ? "\x1bOC" : "\x1b[C";
                    break;
                case Key.Left:
                    sequence = _buffer != null && _buffer.Modes.IsApplicationCursorKeys ? "\x1bOD" : "\x1b[D";
                    break;
                case Key.Home: sequence = "\x1b[H"; break;
                case Key.End: sequence = "\x1b[F"; break;

                // Function Keys
                case Key.F1: sequence = "\x1bOP"; break;
                case Key.F2: sequence = "\x1bOQ"; break;
                case Key.F3: sequence = "\x1bOR"; break;
                case Key.F4: sequence = "\x1bOS"; break;
                case Key.F5: sequence = "\x1b[15~"; break;
                case Key.F6: sequence = "\x1b[17~"; break;
                case Key.F7: sequence = "\x1b[18~"; break;
                case Key.F8: sequence = "\x1b[19~"; break;
                case Key.F9: sequence = "\x1b[20~"; break;
                case Key.F10: sequence = "\x1b[21~"; break;
                case Key.F11: sequence = "\x1b[23~"; break;
                case Key.F12: sequence = "\x1b[24~"; break;
                case Key.Delete: sequence = "\x1b[3~"; break;
                case Key.Insert: sequence = "\x1b[2~"; break;
                case Key.PageUp: sequence = "\x1b[5~"; break;
                case Key.PageDown: sequence = "\x1b[6~"; break;
            }

            if (sequence != null)
            {
                _session.SendInput(sequence);
                return true;
            }

            return false;
        }

        private TerminalBuffer? _buffer;
        public TerminalBuffer? Buffer => _buffer;

        // Coalescing
        private bool _isDirty;
        private DispatcherTimer _renderTimer;
        private readonly DispatcherTimer _cursorBlinkTimer;
        private readonly DispatcherTimer _scrollAnimationTimer;
        private bool _uiTimersRunning;
        private bool _isAttachedToVisualTree;
        private volatile bool _isUiRenderable;
        private bool _cursorBlinkPhase = true;
        private bool _cursorBlinkEnabled = true;
        private bool _bellAudioEnabled = true;
        private bool _bellVisualEnabled = true;
        private bool _isBellFlashActive;
        private bool _enableSmoothScrolling = true;
        private int _targetScrollOffset;
        private CursorStyle _preferredCursorStyle = CursorStyle.Underline;
        private int _lastCursorRow = -1;
        private int _lastCursorCol = -1;
        private long _lastHudUpdateTicks = 0;
        private readonly DispatcherTimer _metricsTimer;

        private void OnRenderTimerTick(object? sender, EventArgs e)
        {
            if (!_isDirty && _buffer?.IsSynchronizedOutput == true)
            {
                _buffer.FlushSynchronizedOutputTimeout();
            }

            if (_showRenderHud)
            {
                long now = DateTime.UtcNow.Ticks;
                if (TimeSpan.FromTicks(now - _lastHudUpdateTicks).TotalMilliseconds >= 100)
                {
                    _lastHudUpdateTicks = now;
                    _isDirty = true;
                }
            }

            if (_isDirty)
            {
                if (!_isUiRenderable)
                {
                    RendererStatistics.RecordHiddenInvalidationRequest();
                    return;
                }

                if (_buffer != null)
                {
                    int cursorRow, cursorCol;
                    long cursorSuppressedUntil;
                    _buffer.Lock.EnterReadLock();
                    try
                    {
                        cursorRow = _buffer.InternalCursorRow;
                        cursorCol = _buffer.InternalCursorCol;
                        cursorSuppressedUntil = _buffer.CursorSuppressedUntilUtcTicks;
                    }
                    finally { _buffer.Lock.ExitReadLock(); }

                    if (cursorRow != _lastCursorRow || cursorCol != _lastCursorCol)
                    {
                        _lastCursorRow = cursorRow;
                        _lastCursorCol = cursorCol;
                        CommandAssistAnchorHintChanged?.Invoke();
                        
                        // Reset blink timer on VT cursor movement (like in Vim)
                        // Ensure we don't override the transient cursor suppression (used by AnsiParser for animated text)
                        long now = DateTime.UtcNow.Ticks;
                        if (_cursorBlinkEnabled && _cursorBlinkTimer.IsEnabled && cursorSuppressedUntil <= now)
                        {
                            TerminalLogger.Log($"[TerminalView] OnRenderTimerTick: VT cursor moved ({_lastCursorRow},{_lastCursorCol}). Resetting blink phase.");
                            _cursorBlinkPhase = true;
                            _cursorBlinkTimer.Stop();
                            _cursorBlinkTimer.Start();
                        }
                        else if (cursorSuppressedUntil > now)
                        {
                            TerminalLogger.Log($"[TerminalView] OnRenderTimerTick: VT cursor moved, but suppressed until {cursorSuppressedUntil} (now {now})");
                        }
                    }
                }

                _isDirty = false;
                InvalidateVisual();
            }
        }

        private void OnCursorBlinkTick(object? sender, EventArgs e)
        {
            if (!_cursorBlinkEnabled)
            {
                if (!_cursorBlinkPhase)
                {
                    _cursorBlinkPhase = true;
                    _isDirty = true;
                }
                return;
            }

            _cursorBlinkPhase = !_cursorBlinkPhase;
            _isDirty = true;
        }

        private void ResetCursorBlink()
        {
            if (!_cursorBlinkEnabled) return;
            
            // Block transient cursor suppression for 200ms to allow smooth TUI navigation
            _buffer?.BlockCursorSuppression(TimeSpan.FromMilliseconds(200));
            
            _cursorBlinkPhase = true;
            _cursorBlinkTimer.Stop();
            _cursorBlinkTimer.Start();
            _isDirty = true;
            InvalidateVisual();
        }

        private void OnScrollAnimationTick(object? sender, EventArgs e)
        {
            if (_scrollOffset == _targetScrollOffset)
            {
                _scrollAnimationTimer.Stop();
                return;
            }

            int delta = _targetScrollOffset - _scrollOffset;
            int step = Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 3);
            ScrollOffset = _scrollOffset + step;
        }

        private void OnMetricsTimerTick(object? sender, EventArgs e)
        {
            if (_buffer == null) return;

            var m = _buffer.GetMemoryMetrics(_glyphCache.EntryCount, _glyphCache.AtlasByteSize);
            
            // Format for easy log parsing/grep
            TerminalLogger.Log(
                $"[TerminalMemory] " +
                $"ScrollbackMB={m.ScrollbackBytes / 1024.0 / 1024.0:F2} | " +
                $"Pages={m.ActivePages} (pooled={m.PooledPages}) | " +
                $"ViewportCells={m.ViewportCells} | " +
                $"GlyphCache={m.GlyphCacheEntries} entries ({m.GlyphCacheAtlasBytes / 1024.0 / 1024.0:F1} MB atlas)"
            );
        }

        public ShellOverride ShellOverride { get; set; } = ShellOverride.Auto;

        public class TextFileDroppedEventArgs : EventArgs
        {
            public string FilePath { get; set; } = string.Empty;
            public string EscapedPath { get; set; } = string.Empty;
        }

        public event EventHandler<TextFileDroppedEventArgs>? TextFileDropped;
        public event Action<string>? TextInputObserved;
        public event Action? BackspaceObserved;
        public event Action? EnterObserved;
        public event Action<string>? PasteObserved;

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.Data.GetFiles() != null || e.Data.Contains(DataFormats.Text))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private async Task OnDropAsync(object? sender, DragEventArgs e)
        {
            if (_session == null) return;

            var files = e.Data.GetFiles();
            if (files != null)
            {
                var paths = new List<string>();
                foreach (var item in files)
                {
                    if (item is IStorageItem storage && storage.Path.IsFile)
                    {
                        paths.Add(storage.Path.LocalPath);
                    }
                }

                if (paths.Count > 0)
                {
                    bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                    
                    bool isWsl = _session.ShellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) ?? false;
                    string? distroName = null;
                    if (isWsl && !string.IsNullOrWhiteSpace(_session.ShellArguments))
                    {
                        var args = _session.ShellArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < args.Length - 1; i++)
                        {
                            if (args[i] == "-d" || args[i] == "--distribution")
                            {
                                distroName = args[i + 1];
                                break;
                            }
                        }
                    }

                    var ctx = new SessionContext
                    {
                        DetectedShell = DetectShellFromCommand(_session.ShellCommand),
                        IsEchoEnabled = _buffer?.Modes.IsEchoEnabled ?? true,
                        IsWslSession = isWsl,
                        WslDistroName = distroName,
                        IsAltScreen = _buffer?.IsAltScreenActive ?? false,
                        ShellOverride = this.ShellOverride
                    };

                    var mapper = new NovaTerminal.Core.Paths.WslPathMapper(new NovaTerminal.Core.Execution.DefaultProcessRunner(), distroName);
                    var result = await DropRouter.HandleDropAsync(ctx, paths, isAlt, mapper);
                    if (result.Handled)
                    {
                        // 1) First check if DropRouter explicitly blocked the input for security
                        if (!string.IsNullOrEmpty(result.ToastMessage) && string.IsNullOrEmpty(result.TextToSend))
                        {
                            // Terminal session echo is disabled and Alt was not held
                            // Do not show the Smart Paste toast. It's unsafe.
                            return;
                        }

                        // Fire smart action event if only 1 text file was dropped
                        if (paths.Count == 1 && NovaTerminal.Core.Input.TextFileDetector.IsTextFile(paths[0]))
                        {
                            var args = new TextFileDroppedEventArgs
                            {
                                FilePath = paths[0],
                                EscapedPath = result.TextToSend ?? string.Empty
                            };
                            TextFileDropped?.Invoke(this, args);
                            return; // Do NOT insert path automatically, wait for user to click Toast
                        }

                        if (!string.IsNullOrEmpty(result.TextToSend))
                        {
                            _session.SendInput(result.TextToSend);
                            PasteObserved?.Invoke(result.TextToSend);
                        }
                        
                        return;
                    }
                }
            }

            if (e.Data.GetText() is string text && !string.IsNullOrWhiteSpace(text))
            {
                _session.SendInput(text);
                PasteObserved?.Invoke(text);
                e.Handled = true;
            }
        }

        private static DetectedShell DetectShellFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return DetectedShell.Unknown;
            if (command.Contains("pwsh", StringComparison.OrdinalIgnoreCase) || command.Contains("powershell", StringComparison.OrdinalIgnoreCase)) return DetectedShell.Pwsh;
            if (command.Contains("cmd", StringComparison.OrdinalIgnoreCase)) return DetectedShell.Cmd;
            if (command.Contains("bash", StringComparison.OrdinalIgnoreCase) || command.Contains("zsh", StringComparison.OrdinalIgnoreCase) || command.Contains("sh", StringComparison.OrdinalIgnoreCase)) return DetectedShell.PosixSh;
            return DetectedShell.Unknown;
        }

        public void TriggerBell()
        {
            if (_bellAudioEnabled)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Console.Beep(880, 35);
                    }
                    else
                    {
                        Console.Beep();
                    }
                }
                catch
                {
                    // Audio bell is best-effort.
                }
            }

            if (_bellVisualEnabled)
            {
                _isBellFlashActive = true;
                _isDirty = true;
                DispatcherTimer.RunOnce(() =>
                {
                    _isBellFlashActive = false;
                    _isDirty = true;
                }, TimeSpan.FromMilliseconds(90));
            }
        }
        // Keep primary font deterministic and monospace-first for box-drawing stability.
        private const string FontFamilyList = "Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Consolas, MesloLGS NF, MesloLGM Nerd Font, Fira Code, Monospace";
        private Typeface _typeface = new Typeface(FontFamilyList, FontStyle.Normal, FontWeight.Normal);
        private double _fontSize = 14;
        private CellMetrics _metrics;
        private double _windowOpacity = 1.0;
        private bool _hasBackgroundImage = false;
        private bool _enableLigatures = false;
        private bool _enableComplexShaping = true;
        private readonly GlyphCache _glyphCache = new();
        private double _lastRenderScalingForRowCache = -1.0;


        private IGlyphTypeface? _glyphTypeface;
        private SharedSKTypeface? _skTypeface;
        private SharedSKFont? _skFont;
        private static readonly bool GlyphDiagnosticsEnabled = IsEnvFlagEnabled("NOVATERM_DIAG_GLYPH");
        private static readonly int[] BoxDrawingProbeCodePoints = { 0x2502, 0x2500, 0x250C, 0x2510, 0x2514, 0x2518, 0x253C };
        private static readonly string[] PreferredMonospaceFonts = { "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", "Cascadia Code" };

        private static readonly string[] FallbackChainNames = {
            "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", // Emojis
            "Segoe UI Symbol", "Symbola",                              // Symbols
            "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", // Monospace-first
            "Cascadia Code", "Fira Code", "MesloLGS NF",                        // Alternate symbol sources
            "Courier New", "Monospace"                                 // Last Resort
        };

        private static readonly List<SKTypeface> FallbackChain = new();
        private static bool _fallbackChainInitialized = false;

        private void EnsureFallbackChain()
        {
            if (_fallbackChainInitialized) return;
            lock (FallbackChain)
            {
                if (_fallbackChainInitialized) return;
                foreach (var name in FallbackChainNames)
                {
                    var tf = SKTypeface.FromFamilyName(name);
                    if (tf != null && tf.FamilyName == name)
                    {
                        FallbackChain.Add(tf);
                    }
                    else
                    {
                        tf?.Dispose();
                    }
                }
                _fallbackChainInitialized = true;
            }
        }

        private readonly ConcurrentDictionary<string, SKTypeface?> _fallbackCache = new();

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

        public int Cols => (_metrics.CellWidth > 0) ? (int)(Math.Max(0, Bounds.Width - 4) / _metrics.CellWidth) : 0;
        public int Rows => (_metrics.CellHeight > 0) ? (int)(Bounds.Height / _metrics.CellHeight) : 0;

        internal CommandAssistPromptHint? GetCommandAssistPromptHint()
        {
            if (_buffer == null || _metrics.CellHeight <= 0 || Rows <= 0)
            {
                return null;
            }

            int visualCursorRow;
            int visibleRows = Rows;
            _buffer.Lock.EnterReadLock();
            try
            {
                visualCursorRow = _buffer.GetVisualCursorRow(_scrollOffset);
            }
            finally
            {
                _buffer.Lock.ExitReadLock();
            }

            if (visibleRows <= 0 || visualCursorRow < 0 || visualCursorRow >= visibleRows)
            {
                return null;
            }

            return new CommandAssistPromptHint(
                VisibleCursorVisualRow: visualCursorRow,
                VisibleRows: visibleRows,
                CellWidth: _metrics.CellWidth,
                CellHeight: _metrics.CellHeight);
        }

        internal void SetMetricsForTest(float cellWidth, float cellHeight)
        {
            _metrics.CellWidth = cellWidth;
            _metrics.CellHeight = cellHeight;
        }

        public void ApplySettings(TerminalSettings settings)
        {
            try
            {
                // Check if font properties changed to avoid unnecessary Skia recreation (prevents crash on rapid opacity changes)
                bool fontChanged = Math.Abs(_fontSize - settings.FontSize) > 0.01 ||
                                   (_typeface.FontFamily.Name != settings.FontFamily);

                _fontSize = settings.FontSize;
                if (fontChanged)
                {
                    _typeface = new Typeface(settings.FontFamily);
                }
                _enableLigatures = settings.EnableLigatures;
                _enableComplexShaping = settings.EnableComplexShaping;
                _windowOpacity = settings.WindowOpacity;
                _hasBackgroundImage = !string.IsNullOrEmpty(settings.BackgroundImagePath) && System.IO.File.Exists(settings.BackgroundImagePath);
                _cursorBlinkEnabled = settings.CursorBlink;
                _preferredCursorStyle = ParseCursorStyle(settings.CursorStyle);
                _bellAudioEnabled = settings.BellAudioEnabled;
                _bellVisualEnabled = settings.BellVisualEnabled;
                _enableSmoothScrolling = settings.SmoothScrolling;
                if (!_cursorBlinkEnabled) _cursorBlinkPhase = true;
                EnsureFallbackChain();

                if (_buffer != null)
                {
                    _buffer.MaxHistory = settings.MaxHistory;
                    _buffer.Modes.CursorStyle = _preferredCursorStyle;
                    _buffer.Modes.IsCursorBlinkEnabled = settings.CursorBlink;

                    // Store old theme for color remapping
                    var oldTheme = _buffer.Theme;

                    // Apply new theme
                    _buffer.Theme = settings.ActiveTheme;

                    // Clear row cache as colors are now baked into SKPictures
                    _rowCache.RequestClear();

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
                    if (_buffer != null && _metrics.CellWidth > 0 && _metrics.CellHeight > 0)
                    {
                        int cols = (int)(Bounds.Width / _metrics.CellWidth);
                        int rows = (int)(Bounds.Height / _metrics.CellHeight);

                        if (cols > 0 && rows > 0 && (cols != _buffer.Cols || rows != _buffer.Rows))
                        {
                            _buffer.Resize(cols, rows);
                            OnResize?.Invoke(cols, rows);
                        }
                    }
                }

                InvalidateVisual();
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("error.log", "\n--- ApplySettings Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
            }
        }

        private static CursorStyle ParseCursorStyle(string? style)
        {
            if (string.IsNullOrWhiteSpace(style)) return CursorStyle.Underline;

            if (Enum.TryParse<CursorStyle>(style, true, out var parsed))
            {
                return parsed;
            }

            return style.Trim().ToLowerInvariant() switch
            {
                "bar" => CursorStyle.Beam,
                "beam" => CursorStyle.Beam,
                "block" => CursorStyle.Block,
                "underline" => CursorStyle.Underline,
                _ => CursorStyle.Underline
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttachedToVisualTree = true;
            RefreshUiTimerState();

            // Ensure char metrics are available immediately upon attachment
            MeasureCharSize();

            _isDirty = true;
            if (_isUiRenderable)
            {
                InvalidateVisual();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _isAttachedToVisualTree = false;
            _isUiRenderable = false;
            StopUiTimers();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty || change.Property == BoundsProperty)
            {
                RefreshUiTimerState();
            }
        }

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);
            if (_session != null && _buffer != null && _buffer.Modes.IsFocusEventReporting)
            {
                _session.SendInput("\x1b[I");
            }
            _isDirty = true;
            InvalidateVisual();
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);
            if (_session != null && _buffer != null && _buffer.Modes.IsFocusEventReporting)
            {
                _session.SendInput("\x1b[O");
            }
            _isDirty = true;
            InvalidateVisual();
        }


        private void ClearSkiaResources()
        {
            _skFont?.Dispose();
            _skFont = null;
            _skTypeface?.Dispose();
            _skTypeface = null;

            _fallbackCache.Clear();
            _rowCache.RequestClear();
            _glyphCache.Clear();
        }

        // Selection state
        private readonly SelectionState _selection = new SelectionState();
        private bool _isSelecting = false;
        private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 51, 153, 255));

        // Session for sending mouse events
        private ITerminalSession? _session;

        public void SetBuffer(TerminalBuffer buffer)
        {
            if (_buffer != null)
            {
                _buffer.OnInvalidate -= InvalidateBuffer; // Idempotent remove
                _buffer.OnScreenSwitched -= OnScreenSwitched;
            }
            _buffer = buffer;
            if (_buffer != null)
            {
                _buffer.OnInvalidate -= InvalidateBuffer; // Ensure no duplicates
                _buffer.OnInvalidate += InvalidateBuffer;
                _buffer.OnScreenSwitched += OnScreenSwitched;
            }

            MeasureCharSize();
            InvalidateVisual();
        }

        private bool _justSwitchedFromAltScreen = false;

        private void OnScreenSwitched(bool isAltScreen)
        {
            // CRITICAL: Clear the row picture cache on every screen switch.
            // AltScreen rows are reused objects whose revision counters can cycle back to
            // previously-cached values, causing stale blank SKPictures to be served.
            _rowCache.RequestClear();

            // When switching back from alt screen to main screen, mark that transition
            // to ensure the next content update resets scroll position
            if (!isAltScreen && _buffer != null)
            {
                _justSwitchedFromAltScreen = true;
                // Schedule scroll to cursor position after switching back to main screen
                // Add a slight delay to handle potential buffering in remote connections
                Dispatcher.UIThread.Post(async () =>
                {
                    // Small delay to allow any buffered output to be processed
                    await Task.Delay(10);
                    // Scroll to show the current cursor position
                    EnsureCursorVisible();
                }, DispatcherPriority.Render);
            }
            else
            {
                _justSwitchedFromAltScreen = false;
            }
        }

        // Property to allow external components to check if we just switched from alt screen
        public bool JustSwitchedFromAltScreen
        {
            get => _justSwitchedFromAltScreen;
            set => _justSwitchedFromAltScreen = value;
        }

        // Method to ensure the cursor is visible in the view
        public void EnsureCursorVisible()
        {
            if (_buffer == null) return;


            // For remote environments (WSL/SSH), after screen transitions, ensure we're at the bottom
            // where the prompt should be, rather than calculating based on cursor position
            // This addresses the issue where new output doesn't scroll properly after mc exits
            if (_justSwitchedFromAltScreen)
            {
                ScrollOffset = 0; // Always scroll to bottom after alt screen switch
                _justSwitchedFromAltScreen = false; // Reset the flag
            }
            else
            {
                // Calculate the ideal scroll offset to show the cursor at the bottom of the viewport
                int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);

                // Only follow the output if we're already near the bottom (within 2 lines)
                // This allows users to scroll up and stay there while still following new output when appropriate
                if (ScrollOffset <= 2)
                {
                    ScrollOffset = 0;
                }
                else
                {
                    // Maintain current scroll position if user has scrolled up
                    // Make sure it's still within valid range
                    ScrollOffset = Math.Min(ScrollOffset, maxScroll);
                }
            }
        }

        private static bool IsEnvFlagEnabled(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SupportsBoxDrawing(SKTypeface typeface)
        {
            foreach (int cp in BoxDrawingProbeCodePoints)
            {
                if (!typeface.ContainsGlyph(cp)) return false;
            }
            return true;
        }

        private static SKTypeface? TryCreateTypeface(string family)
        {
            var tf = SKTypeface.FromFamilyName(family);
            if (tf == null) return null;
            if (string.IsNullOrWhiteSpace(tf.FamilyName))
            {
                tf.Dispose();
                return null;
            }
            return tf;
        }

        private static SKTypeface ResolveMonospacePrimaryTypeface(string configuredFamily, out bool usedFallback)
        {
            usedFallback = false;

            var configured = TryCreateTypeface(configuredFamily);
            if (configured != null)
            {
                return configured;
            }

            configured?.Dispose();
            foreach (string family in PreferredMonospaceFonts)
            {
                var candidate = TryCreateTypeface(family);
                if (candidate == null) continue;
                if (!SupportsBoxDrawing(candidate))
                {
                    candidate.Dispose();
                    continue;
                }

                usedFallback = true;
                return candidate;
            }

            usedFallback = true;
            return SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.FromFamilyName("Monospace") ?? SKTypeface.Default;
        }

        public void MeasureCharSize()
        {
            _rowCache.RequestClear();
            double scaling = VisualRoot?.RenderScaling ?? 1.0;

            // Try to get SKTypeface first as it's our source of truth
            ClearSkiaResources();
            bool skiaSuccess = false;

            try
            {
                string configuredFamily = _typeface.FontFamily.Name;
                SKTypeface primaryTypeface = ResolveMonospacePrimaryTypeface(configuredFamily, out bool usedFallback);
                if (usedFallback && !string.Equals(primaryTypeface.FamilyName, configuredFamily, StringComparison.OrdinalIgnoreCase))
                {
                    TerminalLogger.Log($"[Render][Warn] configured font '{configuredFamily}' unavailable; using '{primaryTypeface.FamilyName}'.");
                    if (GlyphDiagnosticsEnabled)
                    {
                        TerminalLogger.Log($"[GlyphDiag] configured='{configuredFamily}' fallbackPrimary='{primaryTypeface.FamilyName}'");
                    }
                }

                _skTypeface = new SharedSKTypeface(primaryTypeface);
                if (_skTypeface?.Typeface != null)
                {
                    _skFont = new SharedSKFont(new SKFont(_skTypeface.Typeface, (float)_fontSize));
                    if (_skFont.Font != null)
                    {
                        _skFont.Font.Edging = SKFontEdging.Antialias;
                        _skFont.Font.Hinting = SKFontHinting.Normal;

                        var m = _skFont.Font.Metrics;

                        // Authority: Skia metrics
                        float ascent = -m.Ascent;
                        float descent = m.Descent;
                        float leading = m.Leading;
                        float height = ascent + descent + leading;

                        // CELL WIDTH: Authority is 'M' or '0' width in Skia
                        float width = _skFont.Font.MeasureText("M");

                        // PIXEL SNAP: Ensure width/height are exact physical pixel multiples
                        _metrics.CellWidth = (float)(Math.Ceiling(width * scaling) / scaling);
                        _metrics.CellHeight = (float)(Math.Ceiling(height * scaling) / scaling);

                        // Vertical centering logic for baseline
                        float gap = _metrics.CellHeight - (ascent + descent + leading);
                        _metrics.Baseline = (float)(Math.Round((ascent + gap / 2.0f) * scaling) / scaling);

                        _metrics.Ascent = ascent;
                        _metrics.Descent = descent;
                        _metrics.Leading = leading;

                        _glyphTypeface = _typeface.GlyphTypeface;
                        skiaSuccess = true;
                    }
                }
            }
            catch { }

            if (!skiaSuccess)
            {
                // FALLBACK TO AVALONIA (Should be rare)
                var testText = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize * scaling, Brushes.White);
                _metrics.CellWidth = (float)(Math.Ceiling(testText.Width) / scaling);
                _metrics.CellHeight = (float)(Math.Ceiling(testText.Height) / scaling);
                _metrics.Baseline = (float)(Math.Round(testText.Baseline) / scaling);
                _metrics.Ascent = (float)testText.Baseline;
                _metrics.Descent = (float)(testText.Height - testText.Baseline);
                _metrics.Leading = 0;
            }

            MetricsChanged?.Invoke(_metrics.CellWidth, _metrics.CellHeight);
            _glyphTypeface = _typeface.GlyphTypeface;
        }

        public void SetSession(ITerminalSession session)
        {
            _session = session;
            if (_buffer != null)
            {
                _session.AttachBuffer(_buffer);
            }
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
                int newValue = Math.Clamp(value, 0, maxScroll);
                if (_scrollOffset != newValue)
                {
                    _scrollOffset = newValue;
                    _targetScrollOffset = newValue;
                    ScrollStateChanged?.Invoke(_scrollOffset, maxScroll);
                    CommandAssistAnchorHintChanged?.Invoke();
                    InvalidateBuffer();
                }
            }
        }

        public void SetScrollOffset(int offset)
        {
            ScrollOffset = offset;
        }



        // Search state
        private List<SearchMatch> _searchMatches = new List<SearchMatch>();
        private int _activeSearchIndex = -1;

        public event Action<int, int>? SearchStateChanged;

        public void Search(string query, bool useRegex = false, bool caseSensitive = false)
        {
            if (_buffer == null) return;
            _searchMatches = _buffer.FindMatches(query, useRegex, caseSensitive);
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
            if (!_isUiRenderable)
            {
                RendererStatistics.RecordHiddenInvalidationRequest();
            }
            // We rely on _renderTimer (16ms) to check _isDirty and call InvalidateVisual.
            // This acts as a swap-chain throttle, preventing the PTY from flooding the UI thread 
            // with millions of InvalidateVisual calls during cat/heavy output.
        }

        public event Action<int, int>? Ready;
        public event Action<int, int>? OnResize;
        private bool _isReady;

        // Discrete resize: track last sent dimensions to avoid redundant PTY resizes
        private int _lastSentCols = 0;
        private int _lastSentRows = 0;

        // Throttle resize: limit how often we send resize to PTY (interval-based, not debounce)
        private DateTime _lastPtyResizeTime = DateTime.MinValue;
        private DispatcherTimer? _resizeThrottleTimer;
        private int _pendingCols = 0;
        private int _pendingRows = 0;
        private DateTime _pendingResizeStartedAt = DateTime.MinValue;

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
            try
            {
                base.OnSizeChanged(e);

                // Row pictures encode snapped positions for a specific geometry.
                // Clear immediately on size changes so startup/layout transitions
                // cannot reuse stale pictures until the throttled resize path runs.
                _rowCache.RequestClear();

                // Force immediate render pass on any size change to prevent white panes
                InvalidateVisual();

                if (_buffer != null)
                {
                    if (_metrics.CellWidth <= 0 || _metrics.CellHeight <= 0)
                    {
                        MeasureCharSize();
                    }

                    if (_metrics.CellWidth <= 0 || _metrics.CellHeight <= 0) return; // Still zero? Bail.

                    // Padding must match TerminalDrawOperation (PaddingLeft = 4)
                    // We subtract padding from available width to avoid clipping last column
                    int availableWidth = Math.Max(0, (int)e.NewSize.Width - 4);

                    int cols = (int)(availableWidth / _metrics.CellWidth);
                    int rows = (int)(e.NewSize.Height / _metrics.CellHeight);

                    // Enforce minimum dimensions to prevent layout breakage on very small windows
                    cols = Math.Max(cols, 1);
                    rows = Math.Max(rows, 1);

                    if (cols > 0 && rows > 0)
                    {
                        // DISCRETE RESIZE: Only trigger actual resize when cell dimensions change
                        bool dimensionsChanged = (cols != _lastSentCols || rows != _lastSentRows);

                        if (dimensionsChanged)
                        {
                            // Update tracking
                            _lastSentCols = cols;
                            _lastSentRows = rows;
                        }

                        if (!_isReady)
                        {
                            _isReady = true;
                            if (_buffer != null) _buffer.Resize(cols, rows);
                            Ready?.Invoke(cols, rows);

                            // Also trigger initial PTY resize to sync with layout
                            OnResize?.Invoke(cols, rows);
                        }

                        if (dimensionsChanged)
                        {
                            // STRICT INTERVAL THROTTLE: Limit resize dispatch to 60ms
                            _pendingCols = cols;
                            _pendingRows = rows;
                            var now = DateTime.UtcNow;
                            if (_pendingResizeStartedAt == DateTime.MinValue)
                            {
                                _pendingResizeStartedAt = now;
                            }

                            if (_resizeThrottleTimer == null)
                            {
                                _resizeThrottleTimer = new DispatcherTimer(DispatcherPriority.Normal)
                                {
                                    Interval = TimeSpan.FromMilliseconds(60)
                                };
                                _resizeThrottleTimer.Tick += OnResizeThrottleTick;
                            }

                            var elapsed = (now - _lastPtyResizeTime).TotalMilliseconds;

                            if (elapsed >= 60 && !_resizeThrottleTimer.IsEnabled)
                            {
                                // Enough time passed and no pending timer - send immediately
                                SendThrottledResize();
                            }
                            else if (!_resizeThrottleTimer.IsEnabled)
                            {
                                _resizeThrottleTimer.Start();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("error.log", "\n--- OnSizeChanged Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
            }
        }


        private void SendThrottledResize()
        {
            try
            {
                if (_pendingCols > 0 && _pendingRows > 0 && _buffer != null)
                {
                    _lastPtyResizeTime = DateTime.UtcNow;

                    // CRITICAL ORDER: Resize buffer FIRST (synchronously, under lock)
                    // THEN notify PTY (triggers SIGWINCH, new output uses new size)
                    // This prevents race where PTY sends data for new dimensions while buffer is mid-reflow
                    _buffer.Resize(_pendingCols, _pendingRows);
                    _rowCache.MaxEntries = Math.Max(_pendingRows * 3, 50);
                    _rowCache.RequestClear();
                    OnResize?.Invoke(_pendingCols, _pendingRows);
                    if (_pendingResizeStartedAt != DateTime.MinValue)
                    {
                        long latencyMs = (long)Math.Max(0, (DateTime.UtcNow - _pendingResizeStartedAt).TotalMilliseconds);
                        RendererStatistics.RecordResizeDispatchLatency(latencyMs);
                        _pendingResizeStartedAt = DateTime.MinValue;
                    }

                    InvalidateBuffer();
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("error.log", "\n--- SendThrottledResize Exception at " + DateTime.Now + " ---\n" + ex.ToString() + "\n"); } catch { }
            }
        }

        private void OnResizeThrottleTick(object? sender, EventArgs e)
        {
            // Timer fired - send any pending resize
            _resizeThrottleTimer?.Stop();
            SendThrottledResize();
        }

        private void StartUiTimers()
        {
            if (_uiTimersRunning) return;
            _renderTimer.Start();
            _cursorBlinkTimer.Start();
            _uiTimersRunning = true;
            RendererStatistics.RecordTerminalViewTimersStarted();
        }

        private void StopUiTimers()
        {
            if (!_uiTimersRunning) return;

            _renderTimer.Stop();
            _cursorBlinkTimer.Stop();
            _scrollAnimationTimer.Stop();
            _autoScrollTimer?.Stop();
            _resizeThrottleTimer?.Stop();
            _uiTimersRunning = false;
            RendererStatistics.RecordTerminalViewTimersStopped();
        }

        private bool ShouldRunUiTimers()
        {
            return _isAttachedToVisualTree &&
                   IsVisible &&
                   IsEffectivelyVisible &&
                   Bounds.Width > 0 &&
                   Bounds.Height > 0;
        }

        private void RefreshUiTimerState()
        {
            bool shouldRun = ShouldRunUiTimers();
            _isUiRenderable = shouldRun;

            if (shouldRun)
            {
                StartUiTimers();
                if (_isDirty)
                {
                    InvalidateVisual();
                }
                return;
            }

            StopUiTimers();
        }

        public override void Render(DrawingContext context)
        {
            var buffer = _buffer; // Capture local reference to prevent it becoming null mid-render (race condition)
            if (buffer == null)
            {
                // Absolute fallback: draw dark background even without a buffer
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 20), _windowOpacity), new Rect(0, 0, Bounds.Width, Bounds.Height));
                return;
            }

            // Failsafe: Ensure we have fonts, but even if we don't, we should draw a background
            // to avoid "white panes"
            if (_glyphTypeface == null || _skTypeface == null)
            {
                // Fallback background fill if fonts aren't ready
                var theme = buffer.Theme;
                context.FillRectangle(new SolidColorBrush(theme.Background.ToAvaloniaColor(), _windowOpacity), new Rect(0, 0, Bounds.Width, Bounds.Height));
                return;
            }

            // Hide cursor if we are not focused or if blink mode currently hides it.
            bool hideCursor = !IsKeyboardFocusWithin;
            long nowTicks = DateTime.UtcNow.Ticks;

            // Snapshot state under lock to prevent race conditions during resize
            int snapshotRows, snapshotCols, totalLines, cursorRow, cursorCol;
            bool cursorVisibleMode, cursorBlinkMode, cursorSuppressedTemporarily;
            buffer.Lock.EnterReadLock();
            try
            {
                snapshotRows = this.Rows; // Use visual capacity, not buffer size
                snapshotCols = this.Cols;
                totalLines = buffer.InternalTotalLines;
                cursorRow = buffer.InternalCursorRow;
                cursorCol = buffer.InternalCursorCol;
                cursorVisibleMode = buffer.Modes.IsCursorVisible;
                cursorBlinkMode = buffer.Modes.IsCursorBlinkEnabled;
                cursorSuppressedTemporarily = buffer.CursorSuppressedUntilUtcTicks > nowTicks;
            }
            finally { buffer.Lock.ExitReadLock(); }

            if (!cursorVisibleMode) hideCursor = true;
            if (cursorBlinkMode && !_cursorBlinkPhase) hideCursor = true;
            if (cursorSuppressedTemporarily)
            {
                 hideCursor = true;
                 TerminalLogger.Log($"[TerminalView] Render: Cursor suppressed temporarily.");
            }

            // Create and dispatch custom draw op
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            if (Math.Abs(scaling - _lastRenderScalingForRowCache) > 0.0001)
            {
                _lastRenderScalingForRowCache = scaling;
                _rowCache.RequestClear();
            }

            context.Custom(new TerminalDrawOperation(
                Bounds,
                buffer,
                ScrollOffset,
                _selection,
                _searchMatches,
                _activeSearchIndex,
                _metrics,
                _typeface,
                _fontSize,
                _glyphTypeface!,
                _skTypeface,
                _skFont,
                _enableLigatures,
                _fallbackCache,
                FallbackChain.ToArray(),
                _windowOpacity,
                _windowOpacity < 1.0,
                hideCursor,
                scaling,
                snapshotRows,
                snapshotCols,
                totalLines,
                cursorRow,
                cursorCol,
                _rowCache,
                _enableComplexShaping,
                _glyphCache,
                _showRenderHud
            ));

            if (_isBellFlashActive)
            {
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                    new Rect(0, 0, Bounds.Width, Bounds.Height));
            }
        }

        // Mouse event handlers
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_buffer == null) return;

            // If mouse reporting is active, send wheel events to the session
            if (_buffer.IsMouseReportingActive())
            {
                // Wheel Up: button 64, Wheel Down: button 65
                int button = e.Delta.Y > 0 ? 64 : 65;

                // Add modifiers if needed (not strictly required for basic wheel, 
                // but SGR supports it. For now, just button + 32 if motion, but wheel isn't motion)

                var point = e.GetCurrentPoint(this);
                var (row, col) = ScreenToTerminal(point.Position);
                int x = col + 1;
                int y = row + 1;

                if (_buffer.Modes.MouseModeSGR)
                {
                    string sequence = $"\x1b[<{button};{x};{y}M";
                    _session?.SendInput(sequence);
                }
                else
                {
                    // Fallback to legacy X10 if possible, though wheel is mostly an SGR/URXVT extension
                    char buttonChar = (char)(32 + button);
                    char xChar = (char)(32 + Math.Clamp(x, 1, 223));
                    char yChar = (char)(32 + Math.Clamp(y, 1, 223));
                    string sequence = $"\x1b[M{buttonChar}{xChar}{yChar}";
                    _session?.SendInput(sequence);
                }
                e.Handled = true;
                return;
            }

            // Standard scrolling
            // Scroll up (positive) -> Increase Offset
            // Scroll down (negative) -> Decrease Offset
            int delta = (int)(e.Delta.Y * 3); // 3 lines per notch
            if (_enableSmoothScrolling)
            {
                int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
                _targetScrollOffset = Math.Clamp(_targetScrollOffset + delta, 0, maxScroll);
                if (!_scrollAnimationTimer.IsEnabled)
                {
                    _scrollAnimationTimer.Start();
                }
            }
            else
            {
                ScrollOffset += delta;
            }
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

                bool isCtrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (isCtrl && _buffer != null)
                {
                    string? hyperlink = _buffer.GetHyperlinkAbsolute(col, row);
                    if (!string.IsNullOrWhiteSpace(hyperlink) &&
                        Uri.TryCreate(hyperlink, UriKind.Absolute, out var linkUri))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = linkUri.ToString(),
                                UseShellExecute = true
                            });
                            e.Handled = true;
                            return;
                        }
                        catch
                        {
                            // Ignore failed launch attempts.
                        }
                    }
                }

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
                double zoneSize = _metrics.CellHeight * 2; // Drag within top/bottom 2 lines
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
            if (motion && _buffer.Modes.MouseModeAnyEvent)
            {
                button += 32; // Motion indicator
            }

            // Only send if we have SGR mode enabled
            if (_buffer.Modes.MouseModeSGR)
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
                    // FALLBACK: If coordinates exceed X10 limits (223), try force-sending SGR
                    // because X10 cannot represent them. Most modern terminals accept SGR even if not explicitly requested.
                    if (x >= 223 || y >= 223)
                    {
                        char finalChar = pressed ? 'M' : 'm';
                        string sequence = $"\x1b[<{button};{x};{y}{finalChar}";
                        _session.SendInput(sequence);
                        return;
                    }

                    // X10 format: button and coordinates are sent as single bytes offset by 32
                    char buttonChar = (char)(32 + button);
                    char xChar = (char)(32 + x);
                    char yChar = (char)(32 + y);

                    // Clamp Check (redundant now but safe)
                    string seqX10 = $"\x1b[M{buttonChar}{xChar}{yChar}";
                    _session.SendInput(seqX10);
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
                string text;
                _buffer.Lock.EnterReadLock();
                try { text = _selection.GetSelectedText(_buffer); }
                finally { _buffer.Lock.ExitReadLock(); }

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

            _buffer.Lock.EnterReadLock();
            try { return _selection.GetSelectedText(_buffer); }
            finally { _buffer.Lock.ExitReadLock(); }
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

            _buffer.Lock.EnterReadLock();
            try
            {
                // Find word boundaries (non-whitespace characters)
                while (startCol > 0 && !IsWhitespace(_buffer.GetCellAbsolute(startCol - 1, row).Character))
                    startCol--;

                while (endCol < _buffer.Cols - 1 && !IsWhitespace(_buffer.GetCellAbsolute(endCol + 1, row).Character))
                    endCol++;
            }
            finally { _buffer.Lock.ExitReadLock(); }

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

            int col = (int)(position.X / _metrics.CellWidth);
            int visualRow = (int)(position.Y / _metrics.CellHeight);

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
