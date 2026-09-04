using System;
using System.Threading;
using NovaTerminal.Shell;
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

    /// <summary>
    /// Cadence for markdown-presence checks while the panel is closed - the MD button's
    /// visibility rides on this, and it updates on a lazy beat rather than per chunk.
    /// </summary>
    public static readonly TimeSpan PresenceDebounceDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Presence checks run quickly right after a command finishes (the moment the button becomes
    /// relevant) instead of waiting out the lazy cadence.
    /// </summary>
    public static readonly TimeSpan PresenceSoonDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Budget for the presence check: detection needs the tail's shape, not its bulk.</summary>
    public static readonly OutputTailBudget PresenceBudget = new(80, 16 * 1024, 256);

    private readonly Func<TerminalBuffer?> _bufferProvider;
    private readonly Func<ShellIntegrationMark?> _commandOutputStartProvider;
    private readonly Action<Action> _dispatch;
    private readonly Action<string, bool> _onUpdate;
    private readonly Action<bool>? _markdownPresenceChanged;
    private readonly Timer _debounceTimer;

    private bool _enabled;
    private bool _regionActive;
    private ShellIntegrationMark? _heuristicStart;
    private ShellIntegrationMark? _previousHeuristicStart;
    private int _lastPostedHash;
    private int _lastPostedStreaming;
    private int _dedupeStale;
    private int _recentTailFallback;
    private int _generation;
    private int _lastPresence = -1;
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
        Action<string, bool> onUpdate,
        Action<bool>? markdownPresenceChanged = null)
    {
        _bufferProvider = bufferProvider;
        _commandOutputStartProvider = commandOutputStartProvider;
        _dispatch = dispatch;
        _onUpdate = onUpdate;
        _markdownPresenceChanged = markdownPresenceChanged;
        _debounceTimer = new Timer(ReadScheduled, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The shell accepted a command (<c>OSC 133;C</c>). Called on the parse thread, right after
    /// the pane captured the region start.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> gated on <see cref="_enabled"/>: a panel opened while a command is
    /// already running must adopt that command's live region, and the C edge is the only moment
    /// its start is answerable. State set while disabled costs nothing - a disabled tracker never
    /// reads - and <see cref="SetEnabled"/> plus <see cref="FlushNow"/> pick the region up.
    /// </remarks>
    public void NotifyCommandAccepted()
    {
        _regionActive = true;
        _heuristicStart = null;
        AdvanceGeneration();
    }

    /// <summary>
    /// The command finished (<c>OSC 133;D</c>). Called on the parse thread while the grid still
    /// holds the output: the final read happens here, synchronously, and not on a debounce tick.
    /// </summary>
    public void NotifyCommandFinished()
    {
        // Order is load-bearing: resolve the final read's region start while the flag still says
        // a region is live (the provider is a field read; the grid walk below still happens on
        // the parse thread, before any repaint), then flip the flag, THEN bump the generation.
        // Flip-before-bump closes the interleaving where a debounce callback starts between the
        // two: from this instant every background reader either sees a cleared flag - and has
        // nothing to read - or a bumped generation - and is dropped. A streaming:true update in
        // the final generation is therefore impossible.
        ShellIntegrationMark? start = _regionActive ? _commandOutputStartProvider() : null;
        _regionActive = false;
        AdvanceGeneration();

        if (!_enabled)
        {
            // The panel is closed, so D delivered nothing - but this is exactly the moment the
            // MD button's fate changes: the finished output is on screen and the presence check
            // should see it promptly rather than wait out the lazy cadence.
            _debounceTimer.Change(PresenceSoonDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        if (start is not ShellIntegrationMark mark)
        {
            return;
        }

        // The final read runs under the post-bump generation, explicitly bound to the resolved
        // mark rather than to the (now cleared) flag, so it survives this very transition.
        int generation = Volatile.Read(ref _generation);
        TerminalBuffer? buffer = _bufferProvider();
        if (buffer is null || buffer.IsAltScreenActive)
        {
            return;
        }

        if (!CommandOutputReader.TryReadOutputTail(buffer, mark, RegionBudget, out string text))
        {
            return;
        }

        if (Volatile.Read(ref _generation) != generation)
        {
            return;
        }

        DeliverUpdate(text, streaming: false, generation);
    }

    /// <summary>
    /// Grid invalidation. Called on the parse thread for every parser write. While a region is
    /// being tracked it coalesces invalidations into one background read per debounce window;
    /// while the panel is closed it drives the lazy markdown-presence cadence instead, which is
    /// what decides whether the pane's MD button is visible at all.
    /// </summary>
    public void NotifyInvalidate()
    {
        if (_enabled)
        {
            if (_regionActive || _heuristicStart.HasValue)
            {
                _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }
        else
        {
            _debounceTimer.Change(PresenceDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The pane switched between the primary and alternate screen.
    /// </summary>
    /// <remarks>
    /// Entering needs nothing: the panel is hidden, and every read already refuses while the
    /// alternate screen is active. Leaving is the load-bearing half. A command that finished
    /// under a full-screen program had its final read refused for exactly that reason, so
    /// "streaming" is the last thing the panel heard about output that is long done - and
    /// restoring visibility alone would put that stale status back on screen. The corrective
    /// read carries the recent-tail fallback because D cleared the region on its way past,
    /// leaving no edges to resolve; that path posts as finished, which is the status being fixed.
    /// </remarks>
    public void NotifyAltScreenChanged(bool isAltScreenActive)
    {
        if (!isAltScreenActive)
        {
            FlushNow(includeRecentTailFallback: true);
        }
    }

    /// <summary>
    /// Markless fallback: pin the region start from the cursor at Enter time. Called on the UI
    /// thread from the pane's Enter observation, and only when shell integration is not active -
    /// with integration, <see cref="NotifyCommandAccepted"/> owns the region.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> gated on <see cref="_enabled"/>, for the same reason
    /// <see cref="NotifyCommandAccepted"/> is not: Enter is the only moment this row is
    /// answerable, and a panel opened afterwards still needs it. Without the remembered row the
    /// snapshot has to guess where the response starts by recognizing prompt shapes, which only
    /// works for the handful of prompts <c>RecentTailSanitizer</c> knows - a themed prompt
    /// (powerline segments, a right-aligned clock) matches none of them, and the panel then
    /// renders the whole pane, shell banner included. Remembering the row costs a struct while
    /// the panel is closed and removes the guess entirely.
    /// </remarks>
    public void CaptureHeuristicStart()
    {
        if (_regionActive)
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
            // The displaced row is kept as a retry. An Enter that submitted nothing - a bare
            // prompt, which is a keystroke away at any idle shell - pins a row whose region stays
            // empty forever, and it must not bury the response sitting right above it.
            _previousHeuristicStart = _heuristicStart;
            _heuristicStart = start;
        }
    }

    /// <summary>Panel state changed. Enabled trackers read; disabled ones do nothing.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            // Stop any pending region read, then recompute the MD button's presence for the
            // just-closed panel: the toggle must reflect what is on screen now, not whatever
            // was true while the panel was open.
            AdvanceGeneration();
            _debounceTimer.Change(PresenceSoonDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Reads the region now (async, debounced away from the caller's thread). With
    /// <paramref name="includeRecentTailFallback"/>, a read that finds no region being tracked
    /// falls back to a one-shot snapshot of the recent on-screen output - what a panel opened
    /// <i>after</i> the interesting command already finished should show instead of nothing.
    /// </summary>
    public void FlushNow(bool includeRecentTailFallback = false)
    {
        if (_enabled)
        {
            Volatile.Write(ref _recentTailFallback, includeRecentTailFallback ? 1 : 0);
            _debounceTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Session restart: drop the heuristic mark and the streaming flag. The pane calls
    /// this when its shell goes away, since both belong to the shell that is gone.</summary>
    public void Reset()
    {
        _regionActive = false;
        _heuristicStart = null;
        _previousHeuristicStart = null;
        AdvanceGeneration();
    }

    /// <remarks>
    /// The timer goes first, and this does not route through <see cref="SetEnabled"/>: the
    /// disabling branch there arms a <see cref="PresenceSoonDelay"/> tick, so disposing through it
    /// scheduled a callback 250ms into the future and then immediately destroyed the timer that
    /// owed it. The state it wants is set directly instead. Nothing is left to cancel, and the
    /// window in which <see cref="ReadScheduled"/> can find itself running against a disposed
    /// tracker is as narrow as <see cref="Timer"/> allows.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _debounceTimer.Dispose();
        _enabled = false;
        AdvanceGeneration();
    }

    /// <remarks>
    /// <para>
    /// This is a <see cref="Timer"/> callback, so it owns two obligations that are easy to miss.
    /// </para>
    /// <para>
    /// It must not run against a disposed tracker. <see cref="Timer.Dispose()"/> cancels pending
    /// callbacks but does not join one already executing, so a tick can still be in flight when
    /// the pane it reports to goes away - and everything downstream of here ends at a control on
    /// that pane.
    /// </para>
    /// <para>
    /// And it must not let an exception escape. An unhandled exception on a threadpool thread
    /// terminates the process, so without this catch any throw on the read or presence path takes
    /// the whole terminal down - which is what the headless test suite has been demonstrating
    /// several times per run: a stale tick reached <c>AgentOutputToggle.IsVisible</c> across a
    /// swapped dispatcher, Avalonia's <c>VerifyAccess</c> threw, and the escape killed an entire
    /// xUnit collection with no result written for any test in it. The MD button's visibility is
    /// not worth a process for.
    /// </para>
    /// </remarks>
    private void ReadScheduled(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            // While the panel is closed the timer drives markdown-presence detection instead of
            // region reads: the MD button's visibility is the thing that needs updating, and
            // ReadAndPostCore would refuse anyway. This covers the closed-panel ticks that now
            // carry a remembered Enter row, which the earlier shape routed into that refusal.
            if (!_enabled)
            {
                CheckMarkdownPresence(Volatile.Read(ref _generation));
                return;
            }

            ReadAndPostCore(
                streaming: true,
                Volatile.Read(ref _generation),
                fallbackToRecentTail: Interlocked.Exchange(ref _recentTailFallback, 0) == 1);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AgentOutputRegionTracker] read tick threw: {ex}");
        }
    }

    private void ReadAndPostCore(bool streaming, int generation, bool fallbackToRecentTail = false)
    {
        if (!_enabled)
        {
            return;
        }

        // The read is stale the moment the state it was based on changes (C, D, reset, disable).
        // Checked below after the walk, and once more inside DeliverUpdate's dispatched closure:
        // the background read can straddle a transition, and without this a late streaming:true
        // dispatch would park the panel on "streaming…" after D, or resurrect a reset command's
        // output.
        TerminalBuffer? buffer = _bufferProvider();
        if (buffer is null || buffer.IsAltScreenActive)
        {
            return;
        }

        ShellIntegrationMark? start = _regionActive ? _commandOutputStartProvider() : _heuristicStart;
        if (start is not ShellIntegrationMark mark)
        {
            if (fallbackToRecentTail)
            {
                TryPostRecentTailSnapshot(buffer, generation);
            }

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

            // A corrective flush still owes the panel something. Leaving it on its empty state
            // while readable output sits on screen is the worst of the three answers.
            if (fallbackToRecentTail)
            {
                PostCorrectiveFallback(buffer, generation);
            }

            return;
        }

        if (Volatile.Read(ref _generation) != generation)
        {
            return;
        }

        // An empty region is a real answer while a command streams - it has printed nothing yet -
        // but on a corrective flush it means the remembered row did not pan out, and the panel
        // must not blank out over output the user can see above it.
        if (text.Length == 0 && fallbackToRecentTail && !_regionActive)
        {
            PostCorrectiveFallback(buffer, generation);
            return;
        }

        // A corrective flush - the panel just opened, or the alternate screen just closed - that
        // resolved its region from a remembered Enter row rather than a live C mark is looking at
        // history. The markless path has no D edge, so "streaming" there is a guess that nothing
        // would ever retract; post it finished instead. A command that really is still printing
        // re-marks itself on the next debounce tick, one window later.
        bool live = streaming && (_regionActive || !fallbackToRecentTail);
        DeliverUpdate(text, live, generation);
    }

    /// <summary>
    /// What a corrective flush posts when the newest remembered row cannot answer: the row before
    /// it, and failing that the prompt-shape snapshot.
    /// </summary>
    /// <remarks>
    /// The ladder exists because the newest row is not always the interesting one. Pressing Enter
    /// at an idle prompt pins a row that will never hold output, and it lands on top of the row
    /// for the response the user actually wants to read. Retrying the displaced row recovers that
    /// response exactly; the snapshot below it is the guess of last resort, and guesses at prompt
    /// shape are why this ladder is preferred over starting there.
    /// </remarks>
    private void PostCorrectiveFallback(TerminalBuffer buffer, int generation)
    {
        if (_previousHeuristicStart is ShellIntegrationMark previous &&
            CommandOutputReader.TryReadOutputTail(buffer, previous, RegionBudget, out string text) &&
            text.Length > 0)
        {
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            DeliverUpdate(text, streaming: false, generation);
            return;
        }

        TryPostRecentTailSnapshot(buffer, generation);
    }

    /// <summary>
    /// One-shot snapshot of the recent on-screen output, for a panel opened when nothing is being
    /// tracked - the command that produced what is on screen has already finished, and with no
    /// edges left in the grid there is no region to resolve. This is the
    /// <see cref="CommandOutputReader.TryReadRecentTail"/> display contract: what the user can
    /// see above the cursor, prompt lines included, not a vouched-for output region. Posted as
    /// finished; the next command started with the panel open takes over tracking normally.
    /// </summary>
    private void TryPostRecentTailSnapshot(TerminalBuffer buffer, int generation)
    {
        if (!CommandOutputReader.TryReadRecentTail(buffer, RegionBudget, out string text))
        {
            return;
        }

        if (Volatile.Read(ref _generation) != generation)
        {
            return;
        }

        // No edges bound a snapshot, so the raw tail is a fragment of the whole conversation:
        // prompts, echoed commands, and several rounds of responses interleaved. Split at the
        // prompt lines and deliver the latest round's response - in a long agent chat that is
        // "the response", not the whole pane. Indentation is normalized before delivery: agent
        // responses indent examples under list items, and 4-space indents would otherwise parse
        // those sections as code blocks.
        text = RecentTailSanitizer.NormalizeIndentation(RecentTailSanitizer.ExtractLastResponse(text));
        if (text.Length == 0)
        {
            return;
        }

        DeliverUpdate(text, streaming: false, generation);
    }

    /// <summary>
    /// The MD button's visibility check: does the recent on-screen output look like markdown?
    /// Runs only while the panel is closed (open panels keep the toggle visible via their checked
    /// state), and the result crosses to the UI only when it changes.
    /// </summary>
    private void CheckMarkdownPresence(int generation)
    {
        TerminalBuffer? buffer = _bufferProvider();
        if (buffer is null || buffer.IsAltScreenActive)
        {
            return;
        }

        if (!CommandOutputReader.TryReadRecentTail(buffer, PresenceBudget, out string text))
        {
            return;
        }

        if (Volatile.Read(ref _generation) != generation)
        {
            return;
        }

        bool looksLikeMarkdown = MarkdownPresenceDetector.LooksLikeMarkdown(text);
        int flag = looksLikeMarkdown ? 1 : 0;
        if (Interlocked.Exchange(ref _lastPresence, flag) == flag)
        {
            return;
        }

        _dispatch(() => _markdownPresenceChanged?.Invoke(looksLikeMarkdown));
    }

    /// <summary>
    /// Hands one read result to the UI thread, dropping it if its generation has been superseded
    /// meanwhile, and committing the dedupe state only when delivery actually happens.
    /// </summary>
    /// <remarks>
    /// The dedupe commit lives <i>inside</i> the closure on purpose. Committing before dispatch
    /// (the previous shape) let a dropped update still mark its hash and streaming flag as
    /// delivered - so a later, legitimate same-text update in the new generation was suppressed
    /// and the panel stayed stale. Deduping here, on the serialized UI side and behind the
    /// generation check, keeps the two decisions atomic.
    /// </remarks>
    private void DeliverUpdate(string text, bool streaming, int generation)
    {
        _dispatch(() =>
        {
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            // string.GetHashCode is randomized per process but stable within one, which is all a
            // change detector needs. The streaming flag participates in the dedupe on purpose:
            // the text often does not change between the last debounce tick and D, and the
            // panel's status line must still get the "streaming ended" update.
            int hash = text.GetHashCode();
            int streamingFlag = streaming ? 1 : 0;

            // Both reads happen before the decision so the hash is committed either way: a
            // delivery forced through by stale dedupe state still has to leave the posted pair
            // describing what the panel now shows.
            bool sameText = Interlocked.Exchange(ref _lastPostedHash, hash) == hash;
            bool sameStreaming = Volatile.Read(ref _lastPostedStreaming) == streamingFlag;
            bool stale = Interlocked.Exchange(ref _dedupeStale, 0) == 1;
            if (!stale && sameText && sameStreaming)
            {
                return;
            }

            Volatile.Write(ref _lastPostedStreaming, streamingFlag);
            _onUpdate(text, streaming);
        });
    }

    /// <summary>
    /// Supersedes every in-flight read, and marks the dedupe state as belonging to a generation
    /// that is now gone.
    /// </summary>
    /// <remarks>
    /// Staling the dedupe is what makes the next delivery unconditional. The posted hash and
    /// streaming flag only answer "did the panel already hear this <i>within</i> one generation";
    /// across a transition (C, D, reset, panel close) the panel's state can have moved on its
    /// own, so the same pair no longer means the panel is up to date. The concrete leak without
    /// this: reopening the panel on a still-running command whose output has not changed clears
    /// the streaming status, then has its restoring flush dropped as a duplicate - the running
    /// command reads as finished until it next prints. Transitions are rare and meaningful, so
    /// the cost is at most one redundant update each.
    /// </remarks>
    private void AdvanceGeneration()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _dedupeStale, 1);
    }
}
