using System;
using System.Threading;
using NovaTerminal.VT;

namespace NovaTerminal.AgentOutput;

/// <summary>
/// Tracks one pane's current command <i>output region</i> and posts its text as it grows, so the
/// Agent Output panel can render it as markdown while the command streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the region comes from.</b> With shell integration, the pane already captures the
/// <c>OSC 133;C</c> edge into <c>TerminalBuffer.CommandOutputStartMark</c>
/// (<see cref="CommandOutputReader.TryCaptureOutputStart"/>); the tracker resolves that mark at
/// read time and inherits every staleness rule of the marked read. Without shell integration no C
/// edge ever arrives, so <see cref="CaptureHeuristicStart"/> pins the region start from the cursor
/// at Enter time - a weaker, display-only contract (prompt lines can leak in), which is exactly
/// what <see cref="CommandOutputReader.TryReadRecentTail"/> documents for the markless shape.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="NotifyInvalidate"/> fires on the PTY parse thread for every chunk
/// the parser writes; it does nothing but re-arm a debounce timer. The actual grid read runs on a
/// threadpool thread - <see cref="CommandOutputReader"/> takes the buffer read lock itself, and
/// the render snapshot path already reads from off the parse thread the same way. Results cross
/// back through the pane's UI dispatcher. The one synchronous read is <see cref="NotifyCommandFinished"/>:
/// at <c>OSC 133;D</c> the grid still holds the finished command's output, and by the time a
/// debounced read could run the next prompt has painted over its tail. That read runs on the
/// parse thread, bounded by the same budget that caps every other read.
/// </para>
/// <para>
/// <b>Why a hash.</b> A streaming agent repaints churn into the region between debounce ticks;
/// hashing the assembled text skips the no-op updates those ticks would otherwise post, and the
/// panel rebuilds only when the text truly changed.
/// </para>
/// </remarks>
public sealed class AgentOutputRegionTracker : IDisposable
{
    /// <summary>Logical lines kept. Sized for a large agent response, not an error tail.</summary>
    public const int MaxRegionLines = 400;

    /// <summary>Character ceiling on the region text handed to the renderer.</summary>
    public const int MaxRegionChars = 128 * 1024;

    /// <summary>Physical-row backstop, independent of the logical-line budget.</summary>
    public const int MaxRegionRows = 2048;

    /// <summary>The budget every region read walks under.</summary>
    public static readonly OutputTailBudget RegionBudget = new(MaxRegionLines, MaxRegionChars, MaxRegionRows);

    /// <summary>How long grid invalidations are coalesced before a background read runs.</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<TerminalBuffer?> _bufferProvider;
    private readonly Func<ShellIntegrationMark?> _commandOutputStartProvider;
    private readonly Action<Action> _dispatch;
    private readonly Action<string, bool> _onUpdate;
    private readonly Timer _debounceTimer;

    private bool _enabled;
    private bool _regionActive;
    private ShellIntegrationMark? _heuristicStart;
    private int _lastPostedHash;
    private int _lastPostedStreaming;
    private int _disposed;

    /// <param name="bufferProvider">The pane's live buffer, resolved at read time.</param>
    /// <param name="commandOutputStartProvider">
    /// The <c>OSC 133;C</c> region start the pane captured, or null for a markless session.
    /// </param>
    /// <param name="dispatch">Marshals the posted update onto the UI thread.</param>
    /// <param name="onUpdate">
    /// Receives (regionText, isStreaming) on the UI thread. Only called when the text changed.
    /// </param>
    public AgentOutputRegionTracker(
        Func<TerminalBuffer?> bufferProvider,
        Func<ShellIntegrationMark?> commandOutputStartProvider,
        Action<Action> dispatch,
        Action<string, bool> onUpdate)
    {
        _bufferProvider = bufferProvider;
        _commandOutputStartProvider = commandOutputStartProvider;
        _dispatch = dispatch;
        _onUpdate = onUpdate;
        _debounceTimer = new Timer(ReadScheduled, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The shell accepted a command (<c>OSC 133;C</c>). Called on the parse thread, right after
    /// the pane captured the region start.
    /// </summary>
    public void NotifyCommandAccepted()
    {
        if (_enabled)
        {
            _regionActive = true;
            _heuristicStart = null;
        }
    }

    /// <summary>
    /// The command finished (<c>OSC 133;D</c>). Called on the parse thread while the grid still
    /// holds the output: the final read happens here, synchronously, and not on a debounce tick.
    /// </summary>
    public void NotifyCommandFinished()
    {
        if (_enabled)
        {
            // Read before flipping the flag: the C mark the read resolves against is still live
            // at this instant, and the posted update must say the stream just ended.
            ReadAndPost(streaming: false);
        }

        _regionActive = false;
    }

    /// <summary>
    /// Grid invalidation. Called on the parse thread for every parser write; when a region is
    /// being tracked, coalesces invalidations into one background read per debounce window.
    /// </summary>
    public void NotifyInvalidate()
    {
        if (_enabled && (_regionActive || _heuristicStart.HasValue))
        {
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Markless fallback: pin the region start from the cursor at Enter time. Called on the UI
    /// thread from the pane's Enter observation, and only when shell integration is not active -
    /// with integration, <see cref="NotifyCommandAccepted"/> owns the region.
    /// </summary>
    public void CaptureHeuristicStart()
    {
        if (!_enabled || _regionActive)
        {
            return;
        }

        TerminalBuffer? buffer = _bufferProvider();
        if (buffer is null)
        {
            return;
        }

        // A bare capture: the cursor row is the input-line end the reader falls back to when
        // there is no B mark. False on an active alt screen, which is the right refusal.
        if (CommandOutputReader.TryCaptureOutputStart(buffer, null, out ShellIntegrationMark start))
        {
            _heuristicStart = start;
        }
    }

    /// <summary>Panel state changed. Enabled trackers read; disabled ones do nothing.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the region now (async, debounced away from the caller's thread).</summary>
    public void FlushNow()
    {
        if (_enabled)
        {
            _debounceTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Session restart: drop the heuristic mark and the streaming flag. The pane calls
    /// this when its shell goes away, since both belong to the shell that is gone.</summary>
    public void Reset()
    {
        _regionActive = false;
        _heuristicStart = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SetEnabled(false);
        _debounceTimer.Dispose();
    }

    private void ReadScheduled(object? state) => ReadAndPost(streaming: true);

    private void ReadAndPost(bool streaming)
    {
        if (!_enabled)
        {
            return;
        }

        TerminalBuffer? buffer = _bufferProvider();
        if (buffer is null || buffer.IsAltScreenActive)
        {
            return;
        }

        ShellIntegrationMark? start = _regionActive ? _commandOutputStartProvider() : _heuristicStart;
        if (start is not ShellIntegrationMark mark)
        {
            return;
        }

        if (!CommandOutputReader.TryReadOutputTail(buffer, mark, RegionBudget, out string text))
        {
            // A dead generation (scrollback reset) is fatal for the mark, per the reader's
            // contract. The heuristic mark is the tracker's own, so it dies here; the pane's C
            // mark is the pane's to clear.
            if (!_regionActive)
            {
                _heuristicStart = null;
            }

            return;
        }

        // string.GetHashCode is randomized per process but stable within one, which is all a
        // change detector needs. The streaming flag participates in the dedupe on purpose: the
        // text often does not change between the last debounce tick and D, and the panel's
        // status line must still get the "streaming ended" update.
        int hash = text.GetHashCode();
        int streamingFlag = streaming ? 1 : 0;
        if (Interlocked.Exchange(ref _lastPostedHash, hash) == hash &&
            Volatile.Read(ref _lastPostedStreaming) == streamingFlag)
        {
            return;
        }

        Volatile.Write(ref _lastPostedStreaming, streamingFlag);
        _dispatch(() => _onUpdate(text, streaming));
    }
}
