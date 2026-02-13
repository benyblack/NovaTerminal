using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using System;
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
                    PerformSeek((long)slider.Value);
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
            var snapshot = _viewModel.GetNearestSnapshot(targetTimeMs);
            long startProcessingTime = 0;

            if (snapshot != null)
            {
                // Apply snapshot synchronously on UI thread if possible, or Dispatcher
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _buffer.ApplySnapshot(snapshot);
                    // Also resize buffer if snapshot has dims
                    _buffer.Resize(snapshot.Cols, snapshot.Rows);
                });

                // Snapshots don't store their own time in the struct (it was in the event),
                // but GetNearestSnapshot could return it or we assume it's close.
                // ReplayViewModel needs to be updated to return the time too.
                // For now, let's assume we re-process from 0 if no snapshot, 
                // OR we need to know the snapshot time.
                // Hack: We'll assume snapshot time is stored or we re-scan briefly.
                // BETTER: Update ReplayViewModel to return the timestamp.
            }
            else
            {
                // No snapshot? clear buffer?
                await Dispatcher.UIThread.InvokeAsync(() => _buffer.Clear());
                startProcessingTime = 0;
            }

            // 3. One-shot fast-forward to target frame to update screen
            // We run a short replay task
            try
            {
                var runner = new ReplayRunner(_filePath);

                // We need the snapshot's time to set minTimeMs.
                // Let's assume ReplayViewModel.GetNearestSnapshotTuple returns (snap, time).
                var snapEntry = _viewModel.GetNearestSnapshotEntry(targetTimeMs);
                long minTime = snapEntry?.TimeMs ?? 0;

                await runner.RunAsync(
                    onDataCallback: async (data) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => _buffer.WriteContent(System.Text.Encoding.UTF8.GetString(data)));
                    },
                    onResizeCallback: async (w, h) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => _buffer.Resize(w, h));
                    },
                    realtime: false, // Fast forward everything
                    minTimeMs: minTime,
                    fastForwardToMs: targetTimeMs,
                    ct: new CancellationTokenSource(2000).Token // Timeout safety
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
                        await Dispatcher.UIThread.InvokeAsync(() => _buffer.WriteContent(text));
                    },
                    onResizeCallback: async (w, h) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => _buffer.Resize(w, h));
                    },
                    // We don't apply snapshots during playback usually, or maybe we do to self-correct?
                    // For now ignore snapshots in stream during playback to avoid jitter
                    onSnapshotCallback: null,
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
    }
}
