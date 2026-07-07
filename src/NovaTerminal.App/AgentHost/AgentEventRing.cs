using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.AgentHost.Contracts;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// Bounded, thread-safe event log behind <c>waitForEvents</c>
    /// (docs/plans/2026-07-07-agent-host-a2-status-design.md). Sequence
    /// numbers are monotonic across the endpoint's lifetime; when capacity is
    /// exceeded the oldest events are evicted and readers detect the gap via
    /// <see cref="WaitForEventsResult.OldestSeq"/>. Long-pollers park on a
    /// pulse that fires on every append — at-least-once delivery with explicit
    /// loss reporting, no unbounded memory.
    /// </summary>
    public sealed class AgentEventRing
    {
        private readonly object _gate = new();
        private readonly int _capacity;
        private readonly Queue<AgentEventDto> _events = new();
        private long _lastSeq; // 0 = nothing ever appended
        private TaskCompletionSource<bool> _pulse = NewPulse();

        public AgentEventRing(int capacity = AgentHostProtocol.EventRingCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        /// <summary>Appends with the next sequence number and wakes any parked long-pollers.</summary>
        public long Append(Guid paneId, string type, string status, DateTimeOffset timestamp, int? exitCode = null, long? durationMs = null)
        {
            TaskCompletionSource<bool> toPulse;
            long seq;
            lock (_gate)
            {
                seq = ++_lastSeq;
                _events.Enqueue(new AgentEventDto
                {
                    Seq = seq,
                    TimestampMs = timestamp.ToUnixTimeMilliseconds(),
                    PaneId = paneId,
                    Type = type,
                    Status = status,
                    ExitCode = exitCode,
                    DurationMs = durationMs,
                });
                while (_events.Count > _capacity)
                {
                    _events.Dequeue();
                }

                toPulse = _pulse;
                _pulse = NewPulse();
            }

            toPulse.TrySetResult(true);
            return seq;
        }

        /// <summary>Snapshot read of everything past the cursor.</summary>
        public WaitForEventsResult ReadSince(long sinceSeq)
        {
            lock (_gate)
            {
                var events = _events.Where(e => e.Seq > sinceSeq).ToArray();
                return new WaitForEventsResult
                {
                    Events = events,
                    NextSeq = _lastSeq,
                    OldestSeq = _events.Count == 0 ? 0 : _events.Peek().Seq,
                };
            }
        }

        /// <summary>
        /// Returns immediately when events exist past the cursor; otherwise
        /// parks until an append or the timeout (empty result). Cancellation
        /// (client gone, endpoint stopping) propagates.
        /// </summary>
        public async Task<WaitForEventsResult> WaitSinceAsync(long sinceSeq, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                // Capture the pulse BEFORE reading: an append that lands after
                // the read (but before we park) completes this captured pulse,
                // so the wake-up cannot be lost.
                Task pulseTask;
                lock (_gate)
                {
                    pulseTask = _pulse.Task;
                }

                var ready = ReadSince(sinceSeq);
                if (ready.Events.Length > 0)
                {
                    return ready;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return ready; // empty: timeout elapsed
                }

                // The delay gets its own linked cancellation so that when the
                // pulse wins the race, its timer is released immediately rather
                // than running out the full remaining window per wake-up.
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(remaining, delayCts.Token);
                var woke = await Task.WhenAny(pulseTask, delayTask).ConfigureAwait(false);
                delayCts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (woke != pulseTask)
                {
                    return ReadSince(sinceSeq); // timeout; report whatever exists (normally empty)
                }
            }
        }

        private static TaskCompletionSource<bool> NewPulse()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
