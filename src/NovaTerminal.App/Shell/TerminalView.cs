using NovaTerminal.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using NovaTerminal.Pty;

namespace NovaTerminal.Shell
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

    /// <summary>
    /// Where an <c>OSC 133;B</c> mark currently sits on screen. The trusted counterpart to
    /// <see cref="CommandAssistPromptHint"/>: that one reports where the cursor is and lets the
    /// consumer guess the prompt row, this one reports the prompt row itself.
    /// </summary>
    /// <param name="VisibleMarkVisualRow">
    /// Zero-based row inside the viewport holding the first cell of the user's input.
    /// </param>
    /// <remarks>
    /// Deliberately not shape-parity with <see cref="CommandAssistPromptHint"/>: that record also
    /// carries a <c>CellWidth</c> nothing reads, and copying an unread field for symmetry would
    /// only make a second place to keep in sync. Vertical anchoring needs a row, a row count, and
    /// a row height; when a consumer needs a column, add it then.
    /// </remarks>
    public readonly record struct CommandAssistMarkAnchorHint(
        int VisibleMarkVisualRow,
        int VisibleRows,
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
            AddHandler(DragDrop.DropEvent, async (s, e) =>
            {
                try
                {
                    await OnDropAsync(s, e);
                }
                catch (Exception ex)
                {
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
            bool isCtrl = (keyModifiers & KeyModifiers.Control) != 0;

            // Kitty keyboard protocol (disambiguate tier). Must run before every legacy path
            // below - including Alt-sends-ESC - because the protocol replaces those encodings
            // for the keys it claims. When the protocol is off (flags = 0) the encoder returns
            // null for every key and the legacy behavior below is byte-identical to before.
            //
            // _enableKittyKeyboardProtocol is the user-facing kill switch (Blocker 2,
            // TerminalSettings.EnableKittyKeyboardProtocol). When false, this call is skipped
            // entirely so every key falls through to the legacy path below unconditionally,
            // even if a TUI already pushed flag 1 onto the buffer's ModeState.
            if (_enableKittyKeyboardProtocol && TryEncodeKittyKey(key, keyModifiers, out string? kittySequence))
            {
                _session.SendInput(kittySequence!);
                return true;
            }

            // Alt/Meta-sends-ESC must run BEFORE the unconditional Enter/Back/Tab/Escape cases
            // below, which would otherwise swallow Alt+<those> and send the bare control byte.
            if ((keyModifiers & KeyModifiers.Alt) != 0)
            {
                string? altSequence = TerminalInputModeEncoder.EncodeAltKey(key, keyModifiers);
                if (altSequence != null)
                {
                    _session.SendInput(altSequence);
                    return true;
                }
            }

            switch (key)
            {
                case Key.Enter:
                    if (!_session.IsProcessRunning)
                    {
                        // The session has exited (e.g. SSH disconnected) but the dead session
                        // object is kept around so the "[Press Enter to reconnect]" banner works.
                        // Don't swallow Enter into the dead PTY — let it bubble up to
                        // TerminalPane.OnKeyDown, which owns the reconnect-on-Enter logic.
                        return false;
                    }
                    _session.SendInput("\r");
                    EnterObserved?.Invoke();
                    return true;
                case Key.Back:
                    _session.SendInput("\x7f");
                    BackspaceObserved?.Invoke();
                    return true;
                case Key.Tab:
                    // Shift+Tab must emit the back-tab (CBT) sequence ESC [ Z, exactly as
                    // xterm does. TUIs like Claude Code rely on it for reverse navigation
                    // (e.g. cycling permission modes backward); a literal tab breaks that.
                    _session.SendInput((keyModifiers & KeyModifiers.Shift) != 0 ? "\x1b[Z" : "\t");
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
            }

            string? sequence = TerminalInputModeEncoder.EncodeSpecialKey(key, _buffer?.Modes);
            if (sequence != null)
            {
                _session.SendInput(sequence);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Applies the two NovaTerminal-local carve-outs that must win over the kitty keyboard
        /// protocol, then defers to <see cref="TerminalInputModeEncoder.EncodeKittyKey"/>.
        ///
        /// 1. Enter on a dead session still has to bubble up to TerminalPane's
        ///    "[Press Enter to reconnect]" handler instead of being swallowed into a dead PTY.
        /// 2. Ctrl+C with an active selection is copy-to-clipboard, matching the behavior users
        ///    already have; without a selection it is a real Ctrl+C and the protocol encodes it
        ///    as CSI 99;5u (the spec explicitly notes Ctrl+C stops raising SIGINT in this mode).
        /// </summary>
        private bool TryEncodeKittyKey(Key key, KeyModifiers keyModifiers, out string? sequence)
        {
            sequence = null;

            if (key == Key.Enter && _session?.IsProcessRunning != true)
            {
                return false;
            }

            if (key == Key.C && (keyModifiers & KeyModifiers.Control) != 0 && HasSelection())
            {
                return false;
            }

            sequence = TerminalInputModeEncoder.EncodeKittyKey(key, keyModifiers, _buffer?.Modes);
            return sequence != null;
        }

        private TerminalBuffer? _buffer;
        public TerminalBuffer? Buffer => _buffer;

        // Retired inline-image handles: this view OWNS their disposal. The drain lives here —
        // not in TerminalDrawOperation — because that op is also driven by
        // TerminalSnapshotRenderer (agent-host capture) on its own thread, so a draw-op drain
        // could dispose a bitmap another concurrent snapshot is still drawing. The age grace
        // (retire tick vs now) outlives any snapshot's draw window: frames and captures take
        // tens of ms, so a pruned handle is only disposed once nothing can still hold it.
        private const long RetiredImageDisposalGraceMs = 2000;
        private readonly List<object> _retiredImageHandleScratch = new();

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
        // Kill switch for the kitty keyboard protocol's disambiguate tier (Blocker 2, #277
        // review). Defaults true so behavior is unchanged until ApplySettings runs; a fresh
        // TerminalView created outside the settings pipeline (as several tests do) still gets
        // the protocol, matching pre-kill-switch behavior.
        private bool _enableKittyKeyboardProtocol = true;
        private int _targetScrollOffset;
        // Carry fractional high-resolution wheel deltas so precision touchpads / hi-res
        // wheels (which emit sub-notch micro-events) produce one step per notch instead
        // of one per micro-event. Separate accumulators for the two wheel code paths.
        private readonly WheelStepAccumulator _wheelScrollAccumulator = new();
        private readonly WheelStepAccumulator _wheelReportAccumulator = new();
        private double _wheelLinesPerNotch = 3.0;
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
            // Restart only if blink should be running at all: an unconditional Start here
            // would resurrect the timer on an unfocused pane and undo the #126 gating.
            if (ShouldRunCursorBlinkTimer())
            {
                _cursorBlinkTimer.Start();
            }
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

            /// A DropRouter message that applies to this same drop, or null.
            ///
            /// Carried here rather than raised as a separate DropNotice because both render
            /// into the pane's single toast panel: raising both would mean one immediately
            /// overwrites the other, and whichever won, information would be lost. The
            /// reachable case is a single text file dropped on a WSL session where path
            /// mapping fell back to the Windows path - the smart-paste prompt is actionable
            /// and must survive, so the warning rides along with it.
            public string? Notice { get; set; }
        }

        public event EventHandler<TextFileDroppedEventArgs>? TextFileDropped;

        /// Raised with a human-readable explanation when a file drop is refused, or is
        /// accepted with a caveat. DropRouter has always produced these messages
        /// (ToastMessage) but nothing consumed them, so all three - secure-input block,
        /// shell-metacharacter block, and WSL-mapping fallback - were discarded and the drop
        /// appeared to do nothing at all (#182).
        ///
        /// Deliberately a plain message rather than a typed severity: two of the three are
        /// refusals and one is a warning about a successful drop, and the pane presents both
        /// the same way. Splitting them would add a distinction the UI does not make.
        public event Action<string>? DropNotice;

        /// Whether a DropRouter outcome carries a message the user must see.
        ///
        /// Extracted so the contract can be tested against real DropRouter results without
        /// simulating a drag-and-drop gesture. The subtlety worth pinning is that it depends
        /// only on the message: the original code surfaced nothing when a message arrived
        /// *alongside* TextToSend, which is exactly the WSL-mapping-fallback shape, so that
        /// third message was lost even though the other two were at least reachable via the
        /// block branch.
        internal static bool ShouldRaiseDropNotice(string? toastMessage) =>
            !string.IsNullOrEmpty(toastMessage);
        public event Action<string>? TextInputObserved;
        public event Action? BackspaceObserved;
        public event Action? EnterObserved;
        public event Action<string>? PasteObserved;

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private async Task OnDropAsync(object? sender, DragEventArgs e)
        {
            if (_session == null) return;

            var files = e.DataTransfer.TryGetFiles();
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

                    var mapper = new NovaTerminal.Platform.Paths.WslPathMapper(new NovaTerminal.Platform.Execution.DefaultProcessRunner(), distroName);
                    var result = await DropRouter.HandleDropAsync(ctx, paths, isAlt, mapper);
                    if (result.Handled)
                    {
                        // 1) First check if DropRouter explicitly blocked the input for security
                        if (ShouldRaiseDropNotice(result.ToastMessage) && string.IsNullOrEmpty(result.TextToSend))
                        {
                            // Terminal session echo is disabled and Alt was not held.
                            // Do not send anything - but do tell the user why nothing
                            // happened. This used to be a bare `return`, so a blocked drop
                            // was indistinguishable from a drop that silently did nothing
                            // (#182).
                            DropNotice?.Invoke(result.ToastMessage!);
                            return;
                        }

                        // Fire smart action event if only 1 text file was dropped
                        if (paths.Count == 1 && NovaTerminal.Platform.Input.TextFileDetector.IsTextFile(paths[0]))
                        {
                            var args = new TextFileDroppedEventArgs
                            {
                                FilePath = paths[0],
                                EscapedPath = result.TextToSend ?? string.Empty,
                                // This branch returns, so a DropNotice raised below would
                                // never fire for a single text file - the WSL mapping-failure
                                // warning was still being lost here.
                                Notice = ShouldRaiseDropNotice(result.ToastMessage)
                                    ? result.ToastMessage
                                    : null
                            };
                            TextFileDropped?.Invoke(this, args);
                            return; // Do NOT insert path automatically, wait for user to click Toast
                        }

                        if (!string.IsNullOrEmpty(result.TextToSend))
                        {
                            _session.SendInput(result.TextToSend);
                            PasteObserved?.Invoke(result.TextToSend);
                        }

                        // A message can accompany a *successful* drop too: DropRouter sets
                        // one alongside TextToSend when WSL path mapping failed and it fell
                        // back to the Windows path. The path is inserted either way, so this
                        // is a warning rather than a block - and it was the third message
                        // being dropped on the floor here, not just the two blocks above.
                        if (ShouldRaiseDropNotice(result.ToastMessage))
                        {
                            DropNotice?.Invoke(result.ToastMessage!);
                        }

                        return;
                    }
                }
            }

            if (e.DataTransfer.TryGetText() is string text && !string.IsNullOrWhiteSpace(text))
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
        private static readonly string FontFamilyList = $"{BundledFontCatalog.DefaultTerminalFontFamily}, Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Consolas, MesloLGS NF, MesloLGM Nerd Font, Fira Code, Monospace";
        private Typeface _typeface = new Typeface(FontFamilyList, FontStyle.Normal, FontWeight.Normal);
        private double _fontSize = 14;
        private CellMetrics _metrics;
        private double _windowOpacity = 1.0;
        private bool _hasBackgroundImage = false;
        private bool _enableLigatures = false;
        private bool _enableComplexShaping = true;
        private bool _enableLinkDetection = true;
        private readonly GlyphCache _glyphCache = new();
        private double _lastRenderScalingForRowCache = -1.0;
        private TopLevel? _cachedTopLevel;
        private double _cachedRenderScaling = 1.0;


        private GlyphTypeface? _glyphTypeface;
        private SharedSKTypeface? _skTypeface;
        private SharedSKFont? _skFont;
        private static readonly bool GlyphDiagnosticsEnabled = IsEnvFlagEnabled("NOVATERM_DIAG_GLYPH");
        private static readonly int[] BoxDrawingProbeCodePoints = { 0x2502, 0x2500, 0x250C, 0x2510, 0x2514, 0x2518, 0x253C };
        private static readonly string[] PreferredMonospaceFonts = { BundledFontCatalog.DefaultTerminalFontFamily, "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", "Cascadia Code" };

        private static readonly string[] FallbackChainNames = {
            "Segoe UI Symbol", "Symbola",                              // Symbols
            "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", // Emojis
            BundledFontCatalog.DefaultTerminalFontFamily, "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", // Monospace-first
            "Cascadia Code", "Fira Code", "MesloLGS NF",                        // Alternate symbol sources
            "Courier New", "Monospace"                                 // Last Resort
        };

        private static readonly List<SKTypeface> FallbackChain = new();
        private static bool _fallbackChainInitialized = false;

        private static void EnsureFallbackChain()
        {
            if (_fallbackChainInitialized) return;
            lock (FallbackChain)
            {
                if (_fallbackChainInitialized) return;
                foreach (var name in FallbackChainNames)
                {
                    var tf = TryCreateTypeface(name);
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

        /// <summary>
        /// The process-wide glyph fallback chain the live renderer draws with,
        /// for <see cref="TerminalSnapshotRenderer"/>. SKTypeface instances are
        /// immutable and already shared with every draw operation, so handing the
        /// same ones to an off-thread capture adds no new sharing — unlike the
        /// SKFont, which the capture allocates for itself.
        /// </summary>
        internal static SKTypeface[] GetSnapshotFallbackChain()
        {
            EnsureFallbackChain();
            lock (FallbackChain)
            {
                return FallbackChain.ToArray();
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

        /// <summary>Ligature setting currently in effect (for snapshot capture).</summary>
        internal bool EnableLigatures => _enableLigatures;

        /// <summary>Complex-shaping setting currently in effect (for snapshot capture).</summary>
        internal bool EnableComplexShaping => _enableComplexShaping;

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

        /// <summary>
        /// Resolves <paramref name="mark"/> against the buffer and the current scroll position.
        /// </summary>
        /// <remarks>
        /// Returns <c>null</c> whenever the marked row is not on screen — scrolled away, aged out
        /// of history, from a dead coordinate generation, or on the alt screen — and whenever there
        /// is no viewport to be on screen in (no buffer, no metrics, zero rows). "In the viewport"
        /// is the whole contract: a caller that gets a row back may place against it unconditionally.
        /// Because the answer depends on <see cref="ScrollOffset"/>, it must be re-asked on every
        /// placement pass; <see cref="CommandAssistAnchorHintChanged"/> fires on scroll for exactly
        /// that reason.
        /// </remarks>
        internal CommandAssistMarkAnchorHint? GetCommandAssistMarkAnchorHint(ShellIntegrationMark mark)
        {
            if (_buffer == null || _metrics.CellHeight <= 0 || Rows <= 0)
            {
                return null;
            }

            int visibleRows = Rows;
            if (!ShellMarkAnchorResolver.TryResolveVisualRow(_buffer, mark, _scrollOffset, visibleRows, out int visualRow))
            {
                return null;
            }

            return new CommandAssistMarkAnchorHint(
                VisibleMarkVisualRow: visualRow,
                VisibleRows: visibleRows,
                CellHeight: _metrics.CellHeight);
        }

        /// <summary>
        /// Test-only seam for exercising the Ctrl+C-with-selection carve-out (both the legacy
        /// path and its interaction with the kitty keyboard protocol) without driving real
        /// pointer events to build a selection.
        /// </summary>
        internal void SetSelectionForTest(int startRow, int startCol, int endRow, int endCol)
        {
            _selection.Start = (startRow, startCol);
            _selection.End = (endRow, endCol);
            _selection.IsActive = true;
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
                _enableLinkDetection = settings.EnableLinkDetection;
                _enableKittyKeyboardProtocol = settings.EnableKittyKeyboardProtocol;
                // Fall back to the default for non-positive/NaN values; clamp the upper
                // bound so a wild settings value can't drive runaway scroll steps.
                double wheelLinesPerNotch = settings.WheelLinesPerNotch;
                if (double.IsNaN(wheelLinesPerNotch) || wheelLinesPerNotch <= 0)
                {
                    wheelLinesPerNotch = 3.0;
                }
                _wheelLinesPerNotch = Math.Min(wheelLinesPerNotch, 100.0);
                if (!_cursorBlinkEnabled) _cursorBlinkPhase = true;
                // _cursorBlinkEnabled was just reassigned from settings, and it is half of
                // ShouldRunCursorBlinkTimer - so the timer has to be re-evaluated here or
                // toggling the setting would not take effect until the next focus change.
                RefreshCursorBlinkTimerState();
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
                            ResetMouseMotionTracking(); // Issue #269: grid reflowed, cell coords are stale.
                            OnResize?.Invoke(cols, rows);
                        }
                    }
                }

                InvalidateVisual();
            }
            catch (Exception ex)
            {
                TerminalLogger.Error("ApplySettings failed. " + ex);
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
            if (!_metricsTimer.IsEnabled) _metricsTimer.Start();

            _cachedTopLevel = TopLevel.GetTopLevel(this);
            if (_cachedTopLevel != null)
            {
                _cachedRenderScaling = _cachedTopLevel.RenderScaling;
                _cachedTopLevel.ScalingChanged += OnTopLevelScalingChanged;
            }

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
            _metricsTimer.Stop();

            if (_cachedTopLevel != null)
            {
                _cachedTopLevel.ScalingChanged -= OnTopLevelScalingChanged;
                _cachedTopLevel = null;
            }
        }

        private void OnTopLevelScalingChanged(object? sender, EventArgs e)
        {
            _cachedRenderScaling = _cachedTopLevel?.RenderScaling ?? 1.0;
            MeasureCharSize();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty || change.Property == BoundsProperty)
            {
                RefreshUiTimerState();
            }
            else if (change.Property == IsKeyboardFocusWithinProperty)
            {
                // Tracked on IsKeyboardFocusWithin rather than in OnGotFocus/OnLostFocus so
                // the timer follows exactly the same condition Render() uses to decide
                // whether to draw the cursor - including focus landing on a descendant,
                // which the OnGotFocus/OnLostFocus pair does not observe.
                RefreshCursorBlinkTimerState();
            }
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            string? sequence = TerminalInputModeEncoder.EncodeFocusChanged(_buffer?.Modes, isFocused: true);
            if (_session != null && sequence != null)
            {
                _session.SendInput(sequence);
            }
            _isDirty = true;
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            string? sequence = TerminalInputModeEncoder.EncodeFocusChanged(_buffer?.Modes, isFocused: false);
            if (_session != null && sequence != null)
            {
                _session.SendInput(sequence);
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
        private readonly NovaTerminal.VT.Links.UrlDetector _urlDetector = new NovaTerminal.VT.Links.UrlDetector();
        // Hovered link overlay state (transient UI state, never written to the buffer).
        private (int AbsRow, int StartCol, int EndCol, string Uri)? _hoveredLink;
        // One-row memo so we only re-run detection when the pointer moves to a new row.
        private int _hoverScanRow = -1;
        private System.Collections.Generic.IReadOnlyList<NovaTerminal.VT.Links.LinkSpan> _hoverScanSpans =
            System.Array.Empty<NovaTerminal.VT.Links.LinkSpan>();
        private int[] _hoverScanMap = System.Array.Empty<int>();
        private bool _isSelecting = false;
        private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 51, 153, 255));

        // Session for sending mouse events
        private ITerminalSession? _session;

        /// <summary>
        /// Drains retired image handles older than <paramref name="drainTick"/> and disposes
        /// the Skia bitmaps. Called from <see cref="Render"/> (this view's frame boundary) and
        /// internal so tests can drive it deterministically.
        /// </summary>
        internal void DisposeRetiredImageBitmaps(long drainTick)
        {
            var buffer = _buffer;
            if (buffer == null) return;

            if (!buffer.DrainRetiredImageHandles(_retiredImageHandleScratch, drainTick))
            {
                return;
            }

            foreach (var handle in _retiredImageHandleScratch)
            {
                if (handle is SKBitmap bitmap)
                {
                    try
                    {
                        bitmap.Dispose();
                    }
                    catch
                    {
                        // Best effort: never break rendering over a dispose failure.
                    }
                }
            }
            _retiredImageHandleScratch.Clear();
        }

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

            // Issue #269: a newly attached buffer has no notion of "the cell we last
            // reported motion for" - avoid suppressing its first hover/drag report.
            ResetMouseMotionTracking();

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

            // Issue #269: ?47/?1047/?1049 replaces every cell under a stationary pointer, so the
            // incoming application's first hover must report. Without this, a fresh session with
            // empty scrollback (coordinates identical across the switch) has that first hover
            // coalesced away as a duplicate and the app draws no highlight until the pointer
            // crosses a full cell boundary.
            ResetMouseMotionTracking();

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
            var bundled = BundledFontCatalog.TryCreateSkTypeface(family);
            if (bundled != null)
            {
                return bundled;
            }

            var tf = SKTypeface.FromFamilyName(family);
            if (tf == null) return null;
            if (string.IsNullOrWhiteSpace(tf.FamilyName))
            {
                tf.Dispose();
                return null;
            }
            return tf;
        }

        /// <summary>
        /// Resolves the primary monospace typeface for <paramref name="configuredFamily"/>.
        /// Internal rather than private so <see cref="TerminalSnapshotRenderer"/> can
        /// resolve fonts exactly the way the live control does
        /// (<see cref="SnapshotFontResolution.LiveParity"/>).
        /// </summary>
        internal static SKTypeface ResolveMonospacePrimaryTypeface(string configuredFamily, out bool usedFallback)
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
            double scaling = _cachedRenderScaling;

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
                            ResetMouseMotionTracking(); // Issue #269: grid reflowed, cell coords are stale.
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
                TerminalLogger.Error("OnSizeChanged failed. " + ex);
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
                    ResetMouseMotionTracking(); // Issue #269: grid reflowed, cell coords are stale.
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
                TerminalLogger.Error("SendThrottledResize failed. " + ex);
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
            // Blink is gated separately from the render timer: output still arrives on an
            // unfocused pane and must render, but its cursor is not drawn at all.
            RefreshCursorBlinkTimerState();
            _uiTimersRunning = true;
            RendererStatistics.RecordTerminalViewTimersStarted();
        }

        /// The blink timer only earns its keep while the cursor is actually drawn.
        ///
        /// Render() already does `bool hideCursor = !IsKeyboardFocusWithin;`, so on an
        /// unfocused pane every blink tick set _isDirty, the render timer turned that into
        /// an InvalidateVisual, and the resulting frame was pixel-identical - a full render
        /// pass every 530 ms per unfocused pane, forever, for nothing (#126). With several
        /// panes open that is the dominant source of idle wakeups.
        ///
        /// Deliberately not folded into ShouldRunUiTimers(): that gates the render timer
        /// too, and stopping *that* on focus loss would freeze output from a background
        /// shell - a correctness bug, not a saving.
        private bool ShouldRunCursorBlinkTimer() =>
            ShouldRunCursorBlinkTimer(_cursorBlinkEnabled, IsKeyboardFocusWithin);

        /// The rule itself, as a pure function so it can be tested without a visual tree or
        /// a focus manager. `focused` is the caller's IsKeyboardFocusWithin - the same input
        /// Render() uses for `hideCursor`, so the two cannot drift apart.
        internal static bool ShouldRunCursorBlinkTimer(bool blinkEnabled, bool focused) =>
            blinkEnabled && focused;

        private void RefreshCursorBlinkTimerState()
        {
            bool shouldBlink = ShouldRunCursorBlinkTimer();
            if (shouldBlink == _cursorBlinkTimer.IsEnabled)
            {
                return;
            }

            if (shouldBlink)
            {
                // Start solid so the cursor is immediately visible on focus, rather than
                // possibly resuming mid-blink in the hidden phase.
                _cursorBlinkPhase = true;
                _cursorBlinkTimer.Start();
            }
            else
            {
                _cursorBlinkTimer.Stop();
                // Leave the phase solid so a later focus gain cannot briefly show a hidden
                // cursor before the first tick.
                _cursorBlinkPhase = true;
            }
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

            DisposeRetiredImageBitmaps(Environment.TickCount64 - RetiredImageDisposalGraceMs);

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
            var scaling = _cachedRenderScaling;
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

            if (_hoveredLink is { } link && _metrics.CellWidth > 0 && _metrics.CellHeight > 0)
            {
                int displayStart = Math.Max(0, totalLines - buffer.Rows - ScrollOffset);
                int visualRow = link.AbsRow - displayStart;
                if (visualRow >= 0 && visualRow < buffer.Rows)
                {
                    double x = link.StartCol * _metrics.CellWidth;
                    double width = (link.EndCol - link.StartCol + 1) * _metrics.CellWidth;
                    double y = (visualRow + 1) * _metrics.CellHeight - 1.0;
                    var color = buffer.Theme.Foreground.ToAvaloniaColor();
                    context.FillRectangle(
                        new SolidColorBrush(color, _windowOpacity),
                        new Rect(x, y, width, 1.0));
                }
            }

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

            // If mouse reporting is active, forward wheel notches to the session.
            // Accumulate fractional deltas so a high-resolution device emits one
            // WheelUp/WheelDown per notch rather than one per sub-notch micro-event
            // (which otherwise floods a TUI with scroll commands -> runaway scrolling).
            if (_buffer.IsMouseReportingActive())
            {
                // Classic stepped wheel (full notch) forwards 1 event per notch so
                // list/menu TUIs advance one item; only sub-notch precision devices get
                // the smoothing multiplier.
                double reportUnitsPerNotch = WheelStepAccumulator.ReportUnitsPerNotch(e.Delta.Y, _wheelLinesPerNotch);
                int notches = _wheelReportAccumulator.Accumulate(e.Delta.Y, reportUnitsPerNotch);
                if (notches != 0)
                {
                    var point = e.GetCurrentPoint(this);
                    // Viewport-relative, 1-based - the wire coordinate space. Must agree with the
                    // press/release/motion paths so an app sees one consistent coordinate system.
                    var (reportColumn, reportRow) = ToMouseReportCell(point.Position);
                    var button = notches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
                    for (int i = 0; i < Math.Abs(notches); i++)
                    {
                        SendMouseEvent(new TerminalMouseEvent(
                            TerminalMouseEventKind.Wheel,
                            button,
                            reportColumn,
                            reportRow,
                            e.KeyModifiers));
                    }
                }
                e.Handled = true;
                return;
            }

            // Standard scrolling. Accumulate fractional deltas (lines per notch) so
            // sub-notch micro-events are summed line-by-line instead of being truncated
            // to zero each event.
            // Scroll up (positive) -> Increase Offset; scroll down (negative) -> Decrease.
            int delta = _wheelScrollAccumulator.Accumulate(e.Delta.Y, _wheelLinesPerNotch);
            if (delta != 0)
            {
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
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);
            bool leftPressed = point.Properties.IsLeftButtonPressed;

            // Check if application has enabled mouse reporting
            if (_buffer != null && _buffer.IsMouseReportingActive())
            {
                TerminalMouseButton button = GetPressedMouseButton(point.Properties);
                if (button != TerminalMouseButton.None)
                {
                    HandleMousePressAt(point.Position, button, e.KeyModifiers);
                    e.Handled = true;
                    return;
                }
            }

            if (leftPressed)
            {
                // Normal mode: Handle selection
                var (row, col) = ScreenToTerminal(point.Position);

                if (IsLinkActivationModifier(e.KeyModifiers) && _buffer != null)
                {
                    string? uri = ResolveLinkAt(row, col);
                    if (TryOpenLink(uri))
                    {
                        e.Handled = true;
                        return;
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
                TerminalMouseButton button = GetPressedMouseButton(point.Properties);
                HandleMouseMoveAt(point.Position, button, e.KeyModifiers);

                e.Handled = true;
                return;
            }

            if (!_isSelecting)
            {
                UpdateHoveredLink(e.GetCurrentPoint(this).Position);
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
                var point = e.GetCurrentPoint(this);
                var (reportColumn, reportRow) = ToMouseReportCell(point.Position);
                SendMouseEvent(new TerminalMouseEvent(
                    TerminalMouseEventKind.Release,
                    GetReleasedMouseButton(e),
                    reportColumn,
                    reportRow,
                    e.KeyModifiers));
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

        private void UpdateHoveredLink(Avalonia.Point position)
        {
            if (_buffer == null) return;

            var (absRow, col) = ScreenToTerminal(position);

            // 1) Explicit OSC 8 link on this cell always takes precedence.
            string? osc8 = _buffer.GetHyperlinkAbsolute(col, absRow);
            if (!string.IsNullOrWhiteSpace(osc8))
            {
                // Only show as clickable if it would actually open (mirror the click allowlist),
                // so non-openable schemes (e.g. ftp://) don't underline or show the hand cursor.
                if (NovaTerminal.VT.Links.LinkSchemes.IsAllowed(osc8))
                    SetHoveredLink((absRow, col, col, osc8));
                else
                    ClearHoveredLink();
                return;
            }

            // 2) Auto-detected link, if detection is enabled.
            if (_enableLinkDetection)
            {
                if (absRow != _hoverScanRow)
                {
                    var (text, map) = NovaTerminal.VT.Links.RowTextExtractor.Extract(_buffer, absRow);
                    _hoverScanSpans = _urlDetector.Detect(text);
                    _hoverScanMap = map;
                    _hoverScanRow = absRow;
                }

                foreach (var span in _hoverScanSpans)
                {
                    var (startCol, endCol) = NovaTerminal.VT.Links.RowTextExtractor.SpanToColumns(span, _hoverScanMap);
                    if (col >= startCol && col <= endCol)
                    {
                        // Mirror the click allowlist: don't underline schemes that can't open.
                        if (NovaTerminal.VT.Links.LinkSchemes.IsAllowed(span.Uri))
                            SetHoveredLink((absRow, startCol, endCol, span.Uri));
                        else
                            ClearHoveredLink();
                        return;
                    }
                }
            }

            ClearHoveredLink();
        }

        private void SetHoveredLink((int AbsRow, int StartCol, int EndCol, string Uri) link)
        {
            if (_hoveredLink.Equals(link)) return; // no change -> no repaint
            _hoveredLink = link;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            InvalidateVisual();
        }

        private void ClearHoveredLink()
        {
            if (_hoveredLink == null) return;
            _hoveredLink = null;
            Cursor = Avalonia.Input.Cursor.Default;
            InvalidateVisual();
        }

        private static bool IsLinkActivationModifier(KeyModifiers modifiers)
        {
            // Cmd (Meta) on macOS, Ctrl elsewhere — matches platform conventions.
            return OperatingSystem.IsMacOS()
                ? (modifiers & KeyModifiers.Meta) != 0
                : (modifiers & KeyModifiers.Control) != 0;
        }

        private bool TryOpenLink(string? uri)
        {
            if (!NovaTerminal.VT.Links.LinkSchemes.IsAllowed(uri)) return false;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var linkUri)) return false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = linkUri.ToString(),
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                // Ignore failed launch attempts.
                return false;
            }
        }

        // Resolves the link under (absRow, col): OSC 8 first, then a detected span.
        private string? ResolveLinkAt(int absRow, int col)
        {
            if (_buffer == null) return null;

            string? osc8 = _buffer.GetHyperlinkAbsolute(col, absRow);
            if (!string.IsNullOrWhiteSpace(osc8)) return osc8;

            if (!_enableLinkDetection) return null;

            var (text, map) = NovaTerminal.VT.Links.RowTextExtractor.Extract(_buffer, absRow);
            foreach (var span in _urlDetector.Detect(text))
            {
                var (startCol, endCol) = NovaTerminal.VT.Links.RowTextExtractor.SpanToColumns(span, map);
                if (col >= startCol && col <= endCol) return span.Uri;
            }
            return null;
        }

        protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverScanRow = -1;
            ClearHoveredLink();

            // Issue #269: leaving the view means the next OnPointerMoved is a fresh
            // re-entry, not a continuation of whatever cell we last reported. Forget it so
            // re-entry into the same cell reports again instead of being coalesced away.
            ResetMouseMotionTracking();
        }

        // Issue #269: last (row, column) reported for `?1002`/`?1003` motion tracking, used
        // to coalesce to at most one report per cell change instead of flooding the PTY with
        // a report per raw PointerMoved event. Coordinates are 1-based terminal coordinates
        // (matching TerminalMouseEvent), null means "no cell reported yet / tracking reset".
        private (int Row, int Column)? _lastReportedMouseMotionCell;
        private bool _lastMouseMotionAnyEvent;
        private bool _lastMouseMotionButtonEvent;

        /// <summary>
        /// Clears the motion-reporting coalescing state (issue #269) so the next qualifying
        /// pointer move re-reports the current cell rather than being suppressed as a
        /// duplicate of whatever was last sent. Called whenever the "current cell" concept
        /// goes stale: a new buffer is attached (<see cref="SetBuffer"/>), the cell grid is
        /// resized/reflowed, the alternate screen is entered or left
        /// (<see cref="OnScreenSwitched"/>), or the pointer leaves the view
        /// (<see cref="OnPointerExited"/>). Internal (rather than private) so tests can also drive
        /// the reset directly for the sites that are not reachable headlessly (a real
        /// pointer-exited event or layout resize); the SetBuffer and screen-switch sites are
        /// covered through their real call paths.
        /// </summary>
        internal void ResetMouseMotionTracking()
        {
            _lastReportedMouseMotionCell = null;
        }

        /// <summary>
        /// Core motion-reporting logic for <see cref="OnPointerMoved"/> (issue #269), factored
        /// out so it can be exercised directly in tests without a real Avalonia pointer event.
        /// <paramref name="column"/> and <paramref name="row"/> are 1-based terminal
        /// coordinates, matching <see cref="TerminalMouseEvent"/>.
        ///
        /// Under `?1003` (any-event tracking), motion is reported with no buttons held so
        /// hover-driven TUIs (ratatui/bubbletea) can highlight under the cursor; under `?1002`
        /// (button-event tracking) motion is only reported while a button is held, unchanged
        /// from prior behavior. Either way, reports are coalesced to at most one per distinct
        /// cell: Avalonia can raise many PointerMoved events while the cursor sits over a
        /// single terminal cell, and forwarding every one of them would flood the PTY.
        ///
        /// A mode change (1002 vs 1003, either turning on or off) that is OBSERVED BETWEEN TWO
        /// MOTION EVENTS also resets the tracked cell, so a TUI that toggles tracking while the
        /// pointer sits still still gets its next hover reported. This is a sample of
        /// parser-owned <see cref="ModeState"/> taken on the UI thread, not a subscription: a
        /// flip that both begins and ends between two <see cref="OnPointerMoved"/> calls (e.g.
        /// `?1003l` ... `?1003h` inside one redraw) reads as "unchanged" and its first
        /// post-reinit hover in the same cell is still coalesced away. The explicit reset sites
        /// (buffer attach, resize, screen switch, pointer exit) cover the transitions that
        /// actually move content under the pointer; see <see cref="ResetMouseMotionTracking"/>.
        /// </summary>
        internal void HandleMouseMoveCore(TerminalMouseButton button, int column, int row, KeyModifiers modifiers)
        {
            if (_buffer == null) return;

            // ModeState's mouse booleans are written on the PTY reader thread (the parser runs
            // there before output is posted to the dispatcher) and read here on the UI thread
            // without synchronization. Single bool reads cannot tear, and observing a mode one
            // event late only delays a report by one motion event, so no lock is warranted - but
            // it is why the mode-flip detection below is a best-effort sample, not a guarantee.
            ModeState modes = _buffer.Modes;
            bool anyEvent = modes.MouseModeAnyEvent;
            bool buttonEvent = modes.MouseModeButtonEvent;

            if (anyEvent != _lastMouseMotionAnyEvent || buttonEvent != _lastMouseMotionButtonEvent)
            {
                _lastMouseMotionAnyEvent = anyEvent;
                _lastMouseMotionButtonEvent = buttonEvent;
                _lastReportedMouseMotionCell = null;
            }

            bool buttonHeld = button != TerminalMouseButton.None;
            bool wantsReport = anyEvent || (buttonEvent && buttonHeld);
            if (!wantsReport)
            {
                // Neither tracking mode wants this motion. Don't leave a stale cell behind,
                // so re-enabling reporting later doesn't spuriously suppress its first report.
                _lastReportedMouseMotionCell = null;
                return;
            }

            var cell = (row, column);
            if (_lastReportedMouseMotionCell == cell)
            {
                return;
            }

            // Only remember the cell if a report actually went out. A pointer move that lands
            // between SetBuffer and SetSession (no session yet) sends nothing, and recording it
            // anyway would suppress the first real hover in that cell once the session attaches.
            if (SendMouseEvent(new TerminalMouseEvent(TerminalMouseEventKind.Move, button, column, row, modifiers)))
            {
                _lastReportedMouseMotionCell = cell;
            }
        }

        /// <summary>
        /// Sends a button-press report for a view-local pointer <paramref name="position"/>
        /// (issue #269 review). Shares <see cref="ToMouseReportCell"/> with the motion, release
        /// and wheel paths so all four report the same viewport-relative coordinate space.
        /// Internal so tests can drive the real press path without a constructible Avalonia
        /// <c>PointerPressedEventArgs</c>.
        /// </summary>
        internal void HandleMousePressAt(Point position, TerminalMouseButton button, KeyModifiers modifiers)
        {
            var (column, row) = ToMouseReportCell(position);
            SendMouseEvent(new TerminalMouseEvent(TerminalMouseEventKind.Press, button, column, row, modifiers));
        }

        /// <summary>
        /// Motion path entry point taking a view-local pointer <paramref name="position"/>: turns
        /// it into viewport-relative wire coordinates and hands off to
        /// <see cref="HandleMouseMoveCore"/>. This is what <see cref="OnPointerMoved"/> calls, so
        /// tests driving it exercise the production coordinate conversion rather than a copy.
        /// </summary>
        internal void HandleMouseMoveAt(Point position, TerminalMouseButton button, KeyModifiers modifiers)
        {
            var (column, row) = ToMouseReportCell(position);
            HandleMouseMoveCore(button, column, row, modifiers);
        }

        /// <summary>
        /// Encodes and sends a mouse report. Returns <c>true</c> only when bytes were actually
        /// handed to the session, so callers that track "what we last reported" can avoid
        /// recording a report that never happened.
        /// </summary>
        private bool SendMouseEvent(TerminalMouseEvent mouseEvent)
        {
            if (_session == null || _buffer == null) return false;

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(_buffer.Modes, mouseEvent);
            if (sequence != null)
            {
                _session.SendInput(sequence);
                return true;
            }

            return false;
        }

        private static TerminalMouseButton GetPressedMouseButton(PointerPointProperties properties)
        {
            if (properties.IsLeftButtonPressed) return TerminalMouseButton.Left;
            if (properties.IsMiddleButtonPressed) return TerminalMouseButton.Middle;
            if (properties.IsRightButtonPressed) return TerminalMouseButton.Right;
            return TerminalMouseButton.None;
        }

        private static TerminalMouseButton GetReleasedMouseButton(PointerReleasedEventArgs e)
        {
            return e.InitialPressMouseButton switch
            {
                MouseButton.Left => TerminalMouseButton.Left,
                MouseButton.Middle => TerminalMouseButton.Middle,
                MouseButton.Right => TerminalMouseButton.Right,
                _ => TerminalMouseButton.None
            };
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
        /// Sets the system clipboard to the given text, sharing the <see cref="TopLevel"/>
        /// clipboard access path with <see cref="CopySelectionToClipboard"/>. Used by OSC 52
        /// clipboard-write handling (issue #268); the caller (TerminalPane) is responsible for
        /// the settings gate and base64 decoding, this method only performs the actual write.
        /// </summary>
        public async Task<bool> SetClipboardTextAsync(string text)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(text);
                    return true;
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
        /// Converts a view-local pointer position into the 1-based <c>(column, row)</c> pair that
        /// goes on the wire in an xterm mouse report.
        ///
        /// Mouse coordinates are VIEWPORT-relative: row 1 is the top line currently on screen no
        /// matter how much scrollback sits behind it, and the row never exceeds the screen height.
        /// This is deliberately NOT <see cref="ScreenToTerminal"/>, which returns a
        /// scrollback-ABSOLUTE row - the correct space for selection anchors, word/line selection
        /// and hyperlink hit-testing, because those index into scrollback via
        /// <c>GetCellAbsolute</c>/<c>GetHyperlinkAbsolute</c> and must stay pinned to their content
        /// while the view scrolls. Both contracts are needed; they must not be interchanged.
        ///
        /// Issue #269 review fix: every mouse-report call site used to send the absolute row, so a
        /// session with 500 lines of scrollback reported row 501 for the top-left cell (only the
        /// alt screen was accidentally correct, since there TotalLines == Rows). That also defeated
        /// motion coalescing - the dedup key drifted under a physically stationary pointer as
        /// output scrolled - and pushed rows past the legacy encoding's 223-coordinate ceiling.
        /// </summary>
        private (int Column, int Row) ToMouseReportCell(Point position)
        {
            if (_buffer == null || _metrics.CellWidth <= 0 || _metrics.CellHeight <= 0)
            {
                return (1, 1);
            }

            int col = Math.Clamp((int)(position.X / _metrics.CellWidth), 0, _buffer.Cols - 1);
            int visualRow = Math.Clamp((int)(position.Y / _metrics.CellHeight), 0, _buffer.Rows - 1);
            return (col + 1, visualRow + 1);
        }

        /// <summary>
        /// Converts screen coordinates to terminal ABSOLUTE row/col (scrollback-relative: row 0 is
        /// the oldest line still in history). Used by selection and link hit-testing, which need to
        /// stay attached to content across scrolling. Mouse REPORTING must not use this - see
        /// <see cref="ToMouseReportCell"/>.
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
