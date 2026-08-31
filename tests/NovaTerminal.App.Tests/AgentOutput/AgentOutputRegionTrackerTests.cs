using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NovaTerminal.AgentOutput;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The per-pane region tracker: what it posts, when, and what it refuses to post.
/// </summary>
/// <remarks>
/// The tracker is the piece that turns grid invalidations into panel updates, so the tests pin
/// the contract the panel depends on: streaming updates only after the debounce window, the final
/// update synchronously at <c>OSC 133;D</c>, no-op text never posted twice, and every refusal the
/// reader contract implies (alt screen, dead generation, disabled, reset). Driven through the
/// real parser and buffer, like the VT-side reader tests.
/// </remarks>
public sealed class AgentOutputRegionTrackerTests
{
    private const string PromptStart = "\x1b]133;A\x07";
    private const string PromptEnd = "\x1b]133;B\x07";
    private const string AcceptMark = "\x1b]133;C\x07";

    private sealed class Harness : IDisposable
    {
        private readonly object _gate = new();
        private ShellIntegrationMark? _commandLineMark;
        private ShellIntegrationMark? _outputStart;

        public Harness(bool enabled = true, bool deferDispatch = false)
        {
            Buffer = new TerminalBuffer(40, 8);
            Parser = new AnsiParser(Buffer);
            Parser.OnCommandStarted = mark => _commandLineMark = mark;

            Tracker = new AgentOutputRegionTracker(
                () => Buffer,
                () => _outputStart,
                dispatch: action =>
                {
                    // Deferred mode stands in for the UI-thread hop: the update is produced but
                    // not delivered, so tests can transition the tracker in between and deliver
                    // afterwards - the interleaving production hits when a background read
                    // straddles an OSC 133 edge.
                    if (deferDispatch)
                    {
                        lock (_gate)
                        {
                            PendingActions.Add(action);
                        }
                    }
                    else
                    {
                        action();
                    }
                },
                onUpdate: (text, streaming) =>
                {
                    lock (_gate)
                    {
                        Updates.Add((text, streaming));
                    }
                });
            Tracker.SetEnabled(enabled);
        }

        public TerminalBuffer Buffer { get; }

        public AnsiParser Parser { get; }

        public AgentOutputRegionTracker Tracker { get; }

        public List<(string Text, bool Streaming)> Updates { get; } = new();

        /// <summary>Undelivered updates, oldest first, when dispatch is deferred.</summary>
        public List<Action> PendingActions { get; } = new();

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return PendingActions.Count;
                }
            }
        }

        /// <summary>Delivers the queued updates in queue order.</summary>
        public void DeliverPending()
        {
            Action[] toDeliver;
            lock (_gate)
            {
                toDeliver = PendingActions.ToArray();
                PendingActions.Clear();
            }

            foreach (Action action in toDeliver)
            {
                action();
            }
        }

        /// <summary>Delivers the queued updates newest first - the adversarial order a threadpool
        /// read and the UI dispatcher can produce when a background read straddles an edge.</summary>
        public void DeliverPendingNewestFirst()
        {
            Action[] toDeliver;
            lock (_gate)
            {
                toDeliver = PendingActions.ToArray();
                PendingActions.Clear();
            }

            for (int i = toDeliver.Length - 1; i >= 0; i--)
            {
                toDeliver[i]();
            }
        }

        public int UpdateCount
        {
            get
            {
                lock (_gate)
                {
                    return Updates.Count;
                }
            }
        }

        public string LastText
        {
            get
            {
                lock (_gate)
                {
                    return Updates[^1].Text;
                }
            }
        }

        public bool LastStreaming
        {
            get
            {
                lock (_gate)
                {
                    return Updates[^1].Streaming;
                }
            }
        }

        /// <summary>A prompt with shell integration, then a submitted line with the C edge.</summary>
        public Harness Accept(string commandLine)
        {
            Parser.Process(PromptStart + "$ " + PromptEnd);
            Parser.Process(commandLine);
            Parser.Process(AcceptMark);
            _outputStart = CommandOutputReader.TryCaptureOutputStart(Buffer, _commandLineMark, out var start)
                ? start
                : null;
            Tracker.NotifyCommandAccepted();
            return this;
        }

        public Harness Output(params string[] lines)
            => Write("\r\n" + string.Join("\r\n", lines) + "\r\n");

        public Harness Write(string data)
        {
            Parser.Process(data);
            return this;
        }

        /// <summary>The OSC 133;D edge - the tracker's synchronous final read.</summary>
        public void Finish() => Tracker.NotifyCommandFinished();

        /// <summary>
        /// The markless shape: no C edge ever arrives, so the region start is pinned from the
        /// cursor the way the pane does at Enter. Enables the tracker first, matching the pane -
        /// the capture only runs while the panel is open.
        /// </summary>
        public Harness CaptureHeuristicStart()
        {
            Tracker.SetEnabled(true);
            Tracker.CaptureHeuristicStart();
            return this;
        }

        public void Dispose() => Tracker.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "condition not met within the timeout");
    }

    private static int PastDebounceMs
        => (int)AgentOutputRegionTracker.DebounceDelay.TotalMilliseconds + 250;

    [Fact]
    public async Task AcceptedCommand_StreamsRegionTextAsItGrows()
    {
        using var h = new Harness()
            .Accept("agent --task")
            .Output("## Plan", "first chunk");

        h.Tracker.NotifyInvalidate();
        await WaitForAsync(() => h.UpdateCount > 0);

        Assert.Contains("## Plan", h.LastText, StringComparison.Ordinal);
        Assert.Contains("first chunk", h.LastText, StringComparison.Ordinal);
        Assert.True(h.LastStreaming);
    }

    [Fact]
    public void FinishedCommand_PostsTheFinalUpdateSynchronously()
    {
        // No waiting here on purpose: at D the grid is about to be repainted, so the final read
        // must not be deferred to a debounce tick.
        using var h = new Harness()
            .Accept("agent --task")
            .Output("done");

        h.Finish();

        Assert.Equal(1, h.UpdateCount);
        Assert.Contains("done", h.LastText, StringComparison.Ordinal);
        Assert.False(h.LastStreaming);
    }

    [Fact]
    public async Task PanelEnabledWhileACommandIsRunning_AdoptsTheLiveRegion()
    {
        // The C edge arrived while the panel was closed: the tracker must still adopt that
        // command's region when it is enabled mid-flight, instead of showing nothing until the
        // next command starts.
        using var h = new Harness(enabled: false)
            .Accept("agent --task")
            .Output("already streaming");

        h.Tracker.SetEnabled(true);
        h.Tracker.FlushNow();
        await WaitForAsync(() => h.UpdateCount > 0);

        Assert.Contains("already streaming", h.LastText, StringComparison.Ordinal);
        Assert.True(h.LastStreaming);
    }

    [Fact]
    public async Task EnableWithNothingTracked_ShowsTheRecentOnScreenOutput_AsAFinishedSnapshot()
    {
        // The panel opened onto output that already finished: no live C mark, no Enter-time
        // heuristic. The flush falls back to a one-shot recent-tail snapshot so the user sees
        // what is on screen instead of an empty state.
        using var h = new Harness(enabled: false);
        h.Write(PromptStart + "$ " + PromptEnd);
        h.Output("## on screen");

        h.Tracker.SetEnabled(true);
        h.Tracker.FlushNow(includeRecentTailFallback: true);
        await WaitForAsync(() => h.UpdateCount > 0);

        Assert.Contains("## on screen", h.LastText, StringComparison.Ordinal);
        Assert.False(h.LastStreaming);
    }

    [Fact]
    public async Task FlushWithoutTheFallback_WithNothingTracked_PostsNothing()
    {
        using var h = new Harness(enabled: false);
        h.Write(PromptStart + "$ " + PromptEnd);
        h.Output("## on screen");

        h.Tracker.SetEnabled(true);
        h.Tracker.FlushNow();
        await Task.Delay(PastDebounceMs);

        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public async Task RecentTailSnapshot_DoesNotTakeOverTracking()
    {
        // The snapshot is one-shot: the next command started with the panel open takes over
        // tracking normally (heuristic capture), replacing the snapshot text.
        using var h = new Harness(enabled: false);
        h.Write(PromptStart + "$ " + PromptEnd);
        h.Output("old screen content");

        h.Tracker.SetEnabled(true);
        h.Tracker.FlushNow(includeRecentTailFallback: true);
        await WaitForAsync(() => h.UpdateCount > 0);

        h.Tracker.CaptureHeuristicStart();
        h.Output("## new command");
        h.Tracker.NotifyInvalidate();
        await WaitForAsync(() => h.UpdateCount > 1);

        Assert.Contains("## new command", h.LastText, StringComparison.Ordinal);
        Assert.True(h.LastStreaming);
    }

    [Fact]
    public async Task DroppedUpdate_DoesNotPoisonDedupe_ForTheSameTextInANewGeneration()
    {
        // Produced-but-undelivered updates must not commit dedupe state: an update dropped by a
        // generation transition (here, Reset while the UI dispatch is pending) leaves no trace,
        // so the same text delivered in the next generation still reaches the panel.
        using var h = new Harness(enabled: false, deferDispatch: true);
        h.Accept("agent --task").Output("chunk");
        h.Tracker.SetEnabled(true);
        h.Tracker.FlushNow();
        await WaitForAsync(() => h.PendingCount > 0);

        h.Tracker.Reset();
        h.DeliverPending(); // dropped: its generation is gone; hash must NOT be committed
        Assert.Equal(0, h.UpdateCount);

        // A fresh command printing the identical text: the identical hash/streaming pair must
        // not be mistaken for the undelivered update above.
        h.Accept("agent --task").Output("chunk");
        h.Tracker.FlushNow();
        await WaitForAsync(() => h.PendingCount > 0);
        h.DeliverPending();

        Assert.Equal(1, h.UpdateCount);
        Assert.True(h.LastStreaming);
    }

    [Fact]
    public async Task StaleStreamingUpdateDeliveredAfterCompletion_IsDropped()
    {
        // A background read produced while streaming can reach the UI after the D edge; whether
        // it delivers before or after the final update, it must not win - the panel ends on the
        // finished, non-streaming state either way.
        using var h = new Harness(deferDispatch: true);
        h.Accept("agent --task").Output("final text");
        h.Tracker.NotifyInvalidate();
        h.Tracker.FlushNow();
        await WaitForAsync(() => h.PendingCount > 0); // queued: gen G, streaming true

        h.Finish(); // bumps to G+1, queues the final gen G+1 streaming:false update

        // Adversarial order: the stale streaming update delivers last.
        h.DeliverPendingNewestFirst();

        Assert.True(h.UpdateCount >= 1);
        Assert.False(h.LastStreaming);
        Assert.Contains("final text", h.LastText, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishedCommand_PostsFinalState_EvenWhenABackgroundReadWasScheduled()
    {
        // A debounced read armed during streaming overlaps the D edge: D's synchronous read
        // bumps the generation, so whatever that background read dispatches later must be
        // dropped rather than overwrite the finished state. The synchronous harness dispatch
        // makes the ordering deterministic here: the background timer cannot interleave, and
        // this pins that D's own post survives its own generation bump.
        using var h = new Harness()
            .Accept("agent --task")
            .Output("final text");

        h.Tracker.NotifyInvalidate();
        h.Finish();

        Assert.Equal(1, h.UpdateCount);
        Assert.False(h.LastStreaming);
    }

    [Fact]
    public async Task RepeatedInvalidations_WithoutNewOutput_PostNoDuplicateUpdates()
    {
        using var h = new Harness()
            .Accept("agent --task")
            .Output("stable output");

        h.Tracker.NotifyInvalidate();
        await WaitForAsync(() => h.UpdateCount > 0);

        int afterFirst = h.UpdateCount;
        h.Tracker.NotifyInvalidate();
        h.Tracker.NotifyInvalidate();
        h.Tracker.NotifyInvalidate();
        await Task.Delay(PastDebounceMs);

        Assert.Equal(afterFirst, h.UpdateCount);
    }

    [Fact]
    public void InvalidationsBeforeTheDebounceWindow_PostNothing()
    {
        using var h = new Harness()
            .Accept("agent --task")
            .Output("not yet");

        h.Tracker.NotifyInvalidate();

        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public async Task MarklessSession_WithHeuristicStart_TracksRecentOutput()
    {
        using var h = new Harness(enabled: false);
        h.Write(PromptStart + "$ " + PromptEnd); // a prompt, but never a C edge
        h.CaptureHeuristicStart();
        h.Output("raw markdown response");

        h.Tracker.NotifyInvalidate();
        await WaitForAsync(() => h.UpdateCount > 0);

        Assert.Contains("raw markdown response", h.LastText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AltScreenWhileTracked_PostsNoUpdates()
    {
        using var h = new Harness()
            .Accept("vim")
            .Output("some output");

        h.Write("\x1b[?1049h"); // a full-screen program takes over the grid
        h.Tracker.NotifyInvalidate();
        await Task.Delay(PastDebounceMs);

        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public async Task ScrollbackResetWhileTracked_PostsNoUpdates()
    {
        using var h = new Harness()
            .Accept("agent --task")
            .Output("real output");

        // CSI 3J: the coordinate-space epoch changes, and a stale mark must never resolve to
        // someone else's rows. The reader refuses; the tracker stays silent.
        h.Write("\x1b[3J\x1b[H");
        h.Write("unrelated content");
        h.Tracker.NotifyInvalidate();
        await Task.Delay(PastDebounceMs);

        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public void DisabledTracker_IgnoresEverything()
    {
        using var h = new Harness(enabled: false)
            .Accept("agent --task")
            .Output("ignored");

        h.Finish();
        h.Tracker.NotifyInvalidate();

        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public void FinishWhileDisabled_ClearsStreamingWithoutPosting()
    {
        using var h = new Harness();

        // Enabled for the accept, disabled before D: the D read must not fire, and the next
        // invalidation must not resume streaming state from the finished command.
        h.Accept("agent --task");
        h.Output("partial");
        h.Tracker.SetEnabled(false);
        h.Finish();
        h.Tracker.NotifyInvalidate();

        Assert.Equal(0, h.UpdateCount);
    }

    [Fact]
    public async Task Reset_ClearsTheHeuristicRegion()
    {
        using var h = new Harness(enabled: false);
        h.Write(PromptStart + "$ " + PromptEnd);
        h.CaptureHeuristicStart();
        h.Tracker.Reset();
        h.Output("after reset");

        h.Tracker.NotifyInvalidate();
        await Task.Delay(PastDebounceMs);

        Assert.Equal(0, h.UpdateCount);
    }
}
