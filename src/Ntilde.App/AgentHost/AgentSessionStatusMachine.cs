using System;
using System.Collections.Generic;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// Per-session status state machine for the agent-host A2 milestone
    /// (docs/plans/2026-07-07-agent-host-a2-status-design.md).
    ///
    /// Signals arrive from the pane on the UI thread (PTY lifecycle, shell
    /// integration, alt-screen switches); the periodic <see cref="Sweep"/>
    /// contributes time-based transitions (idle, stall) and the PTY
    /// child-process heuristic. All state is guarded by one lock;
    /// <see cref="Snapshot"/> is safe from any thread. Events are collected
    /// under the lock but raised outside it.
    ///
    /// The clock is injectable (same pattern as ShellLifecycleTracker) so
    /// every threshold is deterministic in tests: stall = no output for
    /// <see cref="StallThresholdSeconds"/> while running; idle = at a prompt
    /// with no output for <see cref="IdleThresholdSeconds"/>.
    /// </summary>
    public sealed class AgentSessionStatusMachine
    {
        public const int StallThresholdSeconds = 30;
        public const int IdleThresholdSeconds = 60;

        private readonly object _gate = new();
        private readonly Func<DateTimeOffset> _now;

        // Signal state
        private bool _precise;            // shell integration observed
        private bool _commandInFlight;    // precise tier: between started/finished
        private bool _promptSeen;         // precise tier: a prompt has appeared
        private bool _altScreenActive;
        private bool _hasActiveChildren;  // heuristic tier: from last sweep
        private bool _exited;
        private int? _exitCode;
        private string? _currentCommand;
        private DateTimeOffset? _commandStartedAt;

        // Derived state
        private AgentSessionStatusKind _kind;
        private DateTimeOffset _statusSince;
        private DateTimeOffset _lastOutputAt;
        private bool _stalled;

        /// <summary>Raised outside the lock, in emission order.</summary>
        public event Action<AgentSessionStatusEvent>? EventEmitted;

        public AgentSessionStatusMachine(Func<DateTimeOffset>? nowProvider = null)
        {
            _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
            var now = _now();
            _statusSince = now;
            _lastOutputAt = now;
            // A fresh session has a live process and no known children yet.
            _kind = AgentSessionStatusKind.AwaitingInput;
        }

        // ── Signals (UI thread) ─────────────────────────────────────────────

        public void NotifyOutput()
        {
            RunUnderGate(now =>
            {
                _lastOutputAt = now;
                if (_stalled)
                {
                    // Output resumed: the stall is over. Re-announce the current
                    // status so event consumers see the recovery explicitly.
                    _stalled = false;
                    return new List<AgentSessionStatusEvent>
                    {
                        MakeEvent(AgentSessionEventType.StatusChanged, ComputeKind(now), now),
                    };
                }
                return null;
            });
        }

        public void NotifyPromptReady()
        {
            RunUnderGate(_ =>
            {
                _precise = true;
                _promptSeen = true;
                _commandInFlight = false;
                _currentCommand = null;
                return null;
            });
        }

        public void NotifyCommandAccepted(string? commandText)
        {
            RunUnderGate(_ =>
            {
                _precise = true;
                _currentCommand = string.IsNullOrWhiteSpace(commandText) ? null : commandText.Trim();
                return null;
            });
        }

        public void NotifyCommandStarted()
        {
            RunUnderGate(now =>
            {
                _precise = true;
                _promptSeen = true;
                _commandInFlight = true;
                _commandStartedAt = now;
                // A new command starts a fresh silence episode: without this, a
                // stall from the previous command would leak into this one (an
                // immediate IsStalled=true and no fresh stalled event later).
                _lastOutputAt = now;
                _stalled = false;
                return null;
            });
        }

        public void NotifyCommandFinished(int? exitCode)
        {
            RunUnderGate(now =>
            {
                _precise = true;
                _commandInFlight = false;
                var duration = _commandStartedAt.HasValue ? now - _commandStartedAt.Value : (TimeSpan?)null;
                _commandStartedAt = null;
                var events = new List<AgentSessionStatusEvent>
                {
                    MakeEvent(AgentSessionEventType.CommandFinished, ComputeKind(now), now, exitCode, duration),
                };
                _currentCommand = null;
                return events;
            });
        }

        public void NotifyBell()
        {
            RunUnderGate(now => new List<AgentSessionStatusEvent>
            {
                MakeEvent(AgentSessionEventType.Bell, ComputeKind(now), now),
            });
        }

        public void NotifyAltScreenChanged(bool isAltScreenActive)
        {
            RunUnderGate(_ =>
            {
                _altScreenActive = isAltScreenActive;
                return null;
            });
        }

        public void NotifyExited(int exitCode)
        {
            RunUnderGate(_ =>
            {
                _exited = true;
                _exitCode = exitCode;
                _commandInFlight = false;
                _currentCommand = null;
                return null;
            });
        }

        // ── Sweep (periodic; endpoint-owned in PR2) ─────────────────────────

        /// <summary>
        /// Contributes the time-based transitions and the child-process
        /// heuristic. Idle and stall thresholds only ever fire here, so the
        /// cadence of the caller bounds their latency (1 s in production).
        /// Null means the probe couldn't answer (session initializing or being
        /// swapped): the last known value is kept rather than flapping the
        /// heuristic status through a transient false.
        /// </summary>
        public void Sweep(bool? hasActiveChildProcesses)
        {
            RunUnderGate(now =>
            {
                if (hasActiveChildProcesses.HasValue)
                {
                    _hasActiveChildren = hasActiveChildProcesses.Value;
                }

                if (!_exited
                    && ComputeKind(now) == AgentSessionStatusKind.Running
                    && !_stalled
                    && now - _lastOutputAt >= TimeSpan.FromSeconds(StallThresholdSeconds))
                {
                    _stalled = true;
                    return new List<AgentSessionStatusEvent>
                    {
                        MakeEvent(AgentSessionEventType.Stalled, AgentSessionStatusKind.Running, now),
                    };
                }
                return null;
            });
        }

        // ── Reads ───────────────────────────────────────────────────────────

        public AgentSessionStatusSnapshot Snapshot()
        {
            lock (_gate)
            {
                var now = _now();
                return new AgentSessionStatusSnapshot
                {
                    Kind = ComputeKind(now),
                    Confidence = _precise ? AgentSessionStatusConfidence.Precise : AgentSessionStatusConfidence.Heuristic,
                    ExitCode = _exitCode,
                    CurrentCommand = _currentCommand,
                    StatusSince = _statusSince,
                    LastOutputAt = _lastOutputAt,
                    IsStalled = _stalled,
                };
            }
        }

        // ── Internals ───────────────────────────────────────────────────────

        // Pending events plus a single-drainer flag: signals arrive from the UI
        // thread while Sweep runs on the endpoint's timer thread, so releasing
        // the gate before invoking handlers could deliver events out of the
        // order they were generated. Events are enqueued under the gate and
        // drained by exactly one thread at a time, preserving global order
        // without ever invoking handlers while holding the gate.
        private readonly Queue<AgentSessionStatusEvent> _pendingEvents = new();
        private bool _draining;

        /// <summary>
        /// Mutates under the lock, recomputes the derived status, and raises
        /// any events (mutation-specific ones plus StatusChanged) outside the
        /// lock, in generation order.
        /// </summary>
        private void RunUnderGate(Func<DateTimeOffset, List<AgentSessionStatusEvent>?> mutate)
        {
            lock (_gate)
            {
                var now = _now();
                var before = _kind;
                var produced = mutate(now);
                if (produced != null)
                {
                    foreach (var evt in produced)
                    {
                        _pendingEvents.Enqueue(evt);
                    }
                }

                var after = ComputeKind(now);
                if (after != before)
                {
                    _kind = after;
                    _statusSince = now;
                    if (after != AgentSessionStatusKind.Running)
                    {
                        _stalled = false; // stall is a running-only condition
                    }
                    _pendingEvents.Enqueue(
                        MakeEvent(AgentSessionEventType.StatusChanged, after, now, _exited ? _exitCode : null));
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
                AgentSessionStatusEvent next;
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
                    EventEmitted?.Invoke(next);
                }
                catch
                {
                    lock (_gate) { _draining = false; }
                    throw;
                }
            }
        }

        private AgentSessionStatusKind ComputeKind(DateTimeOffset now)
        {
            if (_exited) return AgentSessionStatusKind.Exited;
            if (_altScreenActive) return AgentSessionStatusKind.Running;

            bool running = _precise ? _commandInFlight : _hasActiveChildren;
            if (running) return AgentSessionStatusKind.Running;

            // At a prompt (precise) or no busy children (heuristic).
            return now - _lastOutputAt >= TimeSpan.FromSeconds(IdleThresholdSeconds)
                ? AgentSessionStatusKind.Idle
                : AgentSessionStatusKind.AwaitingInput;
        }

        private static AgentSessionStatusEvent MakeEvent(
            AgentSessionEventType type,
            AgentSessionStatusKind status,
            DateTimeOffset timestamp,
            int? exitCode = null,
            TimeSpan? duration = null) => new()
            {
                Type = type,
                Status = status,
                Timestamp = timestamp,
                ExitCode = exitCode,
                Duration = duration,
            };
    }
}
