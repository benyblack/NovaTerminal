using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.AgentHost
{
    /// <summary>
    /// One recorded agent acting attempt (A3). Denied attempts are recorded too:
    /// the journal is a visibility surface, so the user can see everything an
    /// agent tried, not only what succeeded.
    /// </summary>
    public sealed record AgentActivityEntry
    {
        public required DateTimeOffset TimestampUtc { get; init; }

        /// <summary>Protocol method (<see cref="Contracts.AgentHostProtocol.Methods"/>), e.g. "sendInput".</summary>
        public required string Method { get; init; }

        /// <summary>Target pane, when the call addressed one.</summary>
        public Guid? PaneId { get; init; }

        /// <summary>Human-readable target (profile name, pane title, or a short summary of the payload).</summary>
        public required string Target { get; init; }

        /// <summary>"ok" for a successful call, otherwise the stable error code that was returned.</summary>
        public required string Outcome { get; init; }
    }

    /// <summary>
    /// Thread-safe bounded ring of recent agent acting attempts (A3). In-memory
    /// only — a visibility surface, not an audit log (the agent's own MCP
    /// transcript is the audit trail). The acting endpoint appends on every
    /// acting request; the UI subscribes to <see cref="EntryAdded"/>.
    /// </summary>
    public sealed class AgentActivityJournal
    {
        /// <summary>Process-wide instance used by the app wiring. Tests construct their own.</summary>
        public static AgentActivityJournal Instance { get; } = new();

        /// <summary>Maximum retained entries; older ones are evicted.</summary>
        public const int Capacity = 200;

        private readonly object _gate = new();
        private readonly Queue<AgentActivityEntry> _entries = new();
        private readonly Func<DateTimeOffset> _now;

        public AgentActivityJournal(Func<DateTimeOffset>? nowProvider = null)
        {
            _now = nowProvider ?? (static () => DateTimeOffset.UtcNow);
        }

        /// <summary>Raised after an entry is appended (may fire on a background/IPC thread).</summary>
        public event Action<AgentActivityEntry>? EntryAdded;

        /// <summary>Records an acting attempt and its outcome. Returns the stored entry.</summary>
        public AgentActivityEntry Record(string method, Guid? paneId, string target, string outcome)
        {
            var entry = new AgentActivityEntry
            {
                TimestampUtc = _now(),
                Method = method,
                PaneId = paneId,
                Target = target ?? string.Empty,
                Outcome = outcome,
            };

            lock (_gate)
            {
                _entries.Enqueue(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }
            }

            EntryAdded?.Invoke(entry);
            return entry;
        }

        /// <summary>Point-in-time snapshot, newest first.</summary>
        public IReadOnlyList<AgentActivityEntry> Snapshot()
        {
            lock (_gate)
            {
                return _entries.Reverse().ToArray();
            }
        }

        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }
    }
}
