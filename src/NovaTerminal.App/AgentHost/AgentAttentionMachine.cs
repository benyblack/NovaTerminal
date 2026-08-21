using System;

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
    /// <see cref="Changed"/> is raised outside the lock. The clock is injectable
    /// so every threshold is deterministic in tests, matching
    /// <see cref="AgentSessionStatusMachine"/>.
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
            AgentAttentionSnapshot? changed = null;
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
                    changed = MakeSnapshot(after);
                }
            }

            if (changed.HasValue)
            {
                Changed?.Invoke(changed.Value);
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
