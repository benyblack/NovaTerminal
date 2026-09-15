using System;
using System.Collections.Generic;

namespace NovaTerminal.AgentHost
{
    /// <summary>How much agent attention a pane is currently getting.</summary>
    public enum AgentAttentionTier
    {
        /// <summary>Nothing recent.</summary>
        Idle,

        /// <summary>An agent read this pane within the last <see cref="AgentAttentionMachine.ReadDecaySeconds"/> seconds.</summary>
        Watched,

        /// <summary>An agent typed into, opened, or closed this pane, and the user has not acknowledged it yet.</summary>
        Wrote,
    }

    /// <summary>Tear-free view of a pane's attention state; safe from any thread.</summary>
    public readonly record struct AgentAttentionSnapshot(
        AgentAttentionTier Tier,
        DateTimeOffset? LastWriteUtc,
        string? LastWriteMethod);

    /// <summary>
    /// Per-pane agent attention state machine
    /// (docs/superpowers/specs/2026-08-21-agent-access-pane-indicator-design.md).
    ///
    /// Reads decay on their own; writes are sticky and are retired only once the
    /// user has plausibly seen them — the pane is focused AND at least
    /// <see cref="WriteFloorSeconds"/> have passed since the write. The floor
    /// exists because an agent can type into the pane the user is already
    /// looking at, where no focus change will ever arrive to acknowledge it;
    /// <see cref="Tick"/> retires those.
    ///
    /// Signals arrive from the endpoint's IPC thread (<see cref="NoteRead"/>,
    /// <see cref="NoteWrote"/>), its timer thread (<see cref="Tick"/>), and the
    /// UI thread (<see cref="NoteFocusChanged"/>). All state is guarded by one
    /// lock; <see cref="Snapshot"/> is safe from any thread and
    /// <see cref="Changed"/> is raised outside the lock, in generation order.
    /// Events are enqueued under the gate and drained by exactly one thread at
    /// a time, preserving global order without invoking handlers while holding
    /// the gate. The clock is injectable so every threshold is deterministic in
    /// tests, matching <see cref="AgentSessionStatusMachine"/>.
    /// </summary>
    public sealed class AgentAttentionMachine
    {
        /// <summary>How long a single read keeps the pane in <see cref="AgentAttentionTier.Watched"/>.</summary>
        public const int ReadDecaySeconds = 3;

        /// <summary>Minimum time a write stays visible, even on an already-focused pane.</summary>
        public const int WriteFloorSeconds = 10;

        private readonly object _gate = new();
        private readonly Func<DateTimeOffset> _now;

        private DateTimeOffset? _lastReadAt;
        private DateTimeOffset? _lastWriteAt;
        private string? _lastWriteMethod;
        private bool _writeAcknowledged;
        private bool _isFocused;
        private AgentAttentionTier _tier = AgentAttentionTier.Idle;

        // Pending events plus a single-drainer flag: signals arrive from the
        // IPC thread, timer thread, and UI thread, so releasing the gate before
        // invoking handlers could deliver events out of the order they were
        // generated. Events are enqueued under the gate and drained by exactly
        // one thread at a time, preserving global order without ever invoking
        // handlers while holding the gate.
        private readonly Queue<AgentAttentionSnapshot> _pendingEvents = new();
        private bool _draining;

        /// <summary>Raised outside the lock whenever the tier changes.</summary>
        public event Action<AgentAttentionSnapshot>? Changed;

        public AgentAttentionMachine(Func<DateTimeOffset>? nowProvider = null)
        {
            _now = nowProvider ?? (static () => DateTimeOffset.UtcNow);
        }

        /// <summary>A pane-addressed read landed (readScreen, readScrollback, getSessionStatus, captureScreen).</summary>
        public void NoteRead() => RunUnderGate(now => _lastReadAt = now);

        /// <summary>A successful write landed. <paramref name="method"/> is the protocol method name.</summary>
        public void NoteWrote(string method) => RunUnderGate(now =>
        {
            _lastWriteAt = now;
            _lastWriteMethod = method;
            _writeAcknowledged = false;
        });

        /// <summary>The owning pane gained or lost focus. Pushed from the UI thread.</summary>
        public void NoteFocusChanged(bool isFocused) => RunUnderGate(_ => _isFocused = isFocused);

        /// <summary>Periodic clock advance: decays reads and retires acknowledged writes.</summary>
        public void Tick() => RunUnderGate(_ => { });

        public AgentAttentionSnapshot Snapshot()
        {
            lock (_gate)
            {
                return MakeSnapshot(ComputeTier(_now()));
            }
        }

        private void RunUnderGate(Action<DateTimeOffset> mutate)
        {
            lock (_gate)
            {
                var now = _now();
                mutate(now);

                // Acknowledgement is evaluated on every signal, not only on focus
                // changes: a write onto an already-focused pane is retired by the
                // tick that carries it past the floor.
                if (_isFocused
                    && _lastWriteAt.HasValue
                    && !_writeAcknowledged
                    && now - _lastWriteAt.Value >= TimeSpan.FromSeconds(WriteFloorSeconds))
                {
                    _writeAcknowledged = true;
                }

                var after = ComputeTier(now);
                if (after != _tier)
                {
                    _tier = after;
                    _pendingEvents.Enqueue(MakeSnapshot(after));
                }

                if (_draining || _pendingEvents.Count == 0)
                {
                    return; // another thread is already delivering, or nothing to deliver
                }
                _draining = true;
            }

            DrainPendingEvents();
        }

        private void DrainPendingEvents()
        {
            while (true)
            {
                AgentAttentionSnapshot next;
                lock (_gate)
                {
                    if (_pendingEvents.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = _pendingEvents.Dequeue();
                }

                try
                {
                    Changed?.Invoke(next);
                }
                catch
                {
                    lock (_gate) { _draining = false; }
                    throw;
                }
            }
        }

        private AgentAttentionTier ComputeTier(DateTimeOffset now)
        {
            if (_lastWriteAt.HasValue && !_writeAcknowledged)
            {
                return AgentAttentionTier.Wrote;
            }
            if (_lastReadAt.HasValue
                && now - _lastReadAt.Value < TimeSpan.FromSeconds(ReadDecaySeconds))
            {
                return AgentAttentionTier.Watched;
            }
            return AgentAttentionTier.Idle;
        }

        private AgentAttentionSnapshot MakeSnapshot(AgentAttentionTier tier)
            => new(tier, _lastWriteAt, _lastWriteMethod);
    }
}
