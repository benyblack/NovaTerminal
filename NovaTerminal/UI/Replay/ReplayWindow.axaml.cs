using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.UI.Replay
{
    public partial class ReplayWindow : Window
    {
        private ReplayViewModel? _viewModel;
        private CancellationTokenSource? _playbackCts;
        private TerminalBuffer? _buffer;
        private AnsiParser? _parser;
        private string? _filePath;
        private bool _isUserScrubbing = false;

        public ReplayWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
        }

        public ReplayWindow(string filePath) : this()
        {
            _filePath = filePath;
            _viewModel = new ReplayViewModel(filePath);
            DataContext = _viewModel;

            _buffer = new TerminalBuffer(80, 24); // Init with default
            _parser = new AnsiParser(_buffer);
            var termView = this.FindControl<TerminalView>("TermView");
            if (termView != null)
            {
                termView.SetBuffer(_buffer);
            }

            var btnPlayPause = this.FindControl<Button>("BtnPlayPause");
            if (btnPlayPause != null) btnPlayPause.Click += (s, e) => TogglePlay();

            var btnClose = this.FindControl<Button>("BtnClose");
            if (btnClose != null) btnClose.Click += (s, e) => Close();

            // Handle slider interaction
            var slider = this.FindControl<Slider>("TimelineSlider");
            if (slider != null)
            {
                // We use PointerPressed/Released to detect scrubbing
                slider.PointerPressed += (s, e) =>
                {
                    _isUserScrubbing = true;
                    if (_viewModel!.IsPlaying) StopPlayback();
                };

                slider.PointerReleased += (s, e) =>
                {
                    _isUserScrubbing = false;
                    _ = PerformSeek((long)slider.Value);
                };

                // Also handle value changes (e.g. clicking on track)
                slider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Value" && _isUserScrubbing)
                    {
                        // Update time display while dragging, but don't seek heavy yet
                        _viewModel!.CurrentTimeMs = (long)(slider.Value);
                    }
                };
            }

            var comboSpeed = this.FindControl<ComboBox>("ComboSpeed");
            if (comboSpeed != null)
            {
                comboSpeed.SelectionChanged += (s, e) => ApplySelectedPlaybackSpeed(comboSpeed);
                ApplySelectedPlaybackSpeed(comboSpeed);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void TogglePlay()
        {
            if (_viewModel == null) return;

            if (_viewModel.IsPlaying)
            {
                StopPlayback();
            }
            else
            {
                StartPlayback();
            }
        }

        private void StartPlayback()
        {
            if (_viewModel == null || _filePath == null) return;

            _viewModel.IsPlaying = true;
            this.FindControl<Button>("BtnPlayPause")!.Content = "⏸";
            _playbackCts = new CancellationTokenSource();

            Task.Run(() => PlaybackLoop(_playbackCts.Token));
        }

        private void StopPlayback()
        {
            if (_viewModel == null) return;

            _viewModel.IsPlaying = false;
            this.FindControl<Button>("BtnPlayPause")!.Content = "▶";
            _playbackCts?.Cancel();
        }

        private async Task PerformSeek(long targetTimeMs)
        {
            if (_viewModel == null || _buffer == null || _filePath == null) return;

            // 1. Stop any running playback
            StopPlayback();
            _viewModel.CurrentTimeMs = targetTimeMs;

            // 2. Find nearest snapshot
            var snapEntry = _viewModel.GetNearestSnapshotEntry(targetTimeMs);

            if (snapEntry != null)
            {
                // Apply snapshot synchronously on UI thread if possible, or Dispatcher
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _buffer.ApplySnapshot(snapEntry.Snapshot);
                    // Also resize buffer if snapshot has dims
                    _buffer.Resize(snapEntry.Snapshot.Cols, snapEntry.Snapshot.Rows);
                });
            }
            else
            {
                // No snapshot? clear buffer?
                await Dispatcher.UIThread.InvokeAsync(() => _buffer.Clear());
            }

            // 3. One-shot fast-forward to target frame to update screen
            // We run a short replay task
            try
            {
                var runner = new ReplayRunner(_filePath);

                long minTime = snapEntry?.TimeMs ?? 0;
                using var seekCts = new CancellationTokenSource(2000);

                await runner.RunAsync(
                    onDataCallback: async (data) =>
                    {
                        var text = System.Text.Encoding.UTF8.GetString(data);
                        await Dispatcher.UIThread.InvokeAsync(() => _parser?.Process(text));
                    },
                    onResizeCallback: async (w, h) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => _buffer.Resize(w, h));
                    },
                    realtime: false, // Fast forward everything
                    minTimeMs: minTime,
                    fastForwardToMs: targetTimeMs,
                    ct: seekCts.Token // Timeout safety
                );
            }
            catch { }
        }

        private async Task PlaybackLoop(CancellationToken ct)
        {
            if (_filePath == null || _viewModel == null || _buffer == null) return;

            try
            {
                var runner = new ReplayRunner(_filePath);
                long startTimeMs = _viewModel.CurrentTimeMs;

                // Determine starting state (Snapshot)
                var snapEntry = _viewModel.GetNearestSnapshotEntry(startTimeMs);
                long minTime = snapEntry?.TimeMs ?? 0;

                if (snapEntry != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _buffer.ApplySnapshot(snapEntry.Snapshot));
                }
                else
                {
                    // No snapshot before this point, clear and start from 0
                    if (minTime == 0) await Dispatcher.UIThread.InvokeAsync(() => _buffer.Clear());
                }

                await runner.RunAsync(
                    onDataCallback: async (data) =>
                    {
                        var text = System.Text.Encoding.UTF8.GetString(data);
                        await Dispatcher.UIThread.InvokeAsync(() => _parser?.Process(text));
                    },
                    onResizeCallback: async (w, h) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => _buffer.Resize(w, h));
                    },
                    // We don't apply snapshots during playback usually, or maybe we do to self-correct?
                    // For now ignore snapshots in stream during playback to avoid jitter
                    onSnapshotCallback: null,
                    onTimeUpdate: async (timeMs) =>
                    {
                        if (_isUserScrubbing) return;
                        // Throttle UI updates or just post
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!_isUserScrubbing) _viewModel.CurrentTimeMs = timeMs;
                        });
                    },
                    realtime: true,
                    minTimeMs: minTime,
                    fastForwardToMs: startTimeMs,
                    playbackSpeed: _viewModel.PlaybackSpeed,
                    ct: ct
                );
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReplayWindow] Playback Error: {ex.Message}");
            }
            finally
            {
                Dispatcher.UIThread.Post(StopPlayback);
            }
        }

        private void ApplySelectedPlaybackSpeed(ComboBox comboSpeed)
        {
            if (_viewModel == null) return;
            if (comboSpeed.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag) return;

            if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
            {
                _viewModel.PlaybackSpeed = speed;
            }
        }
    }
}
