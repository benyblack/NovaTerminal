using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Core
{
    public enum PaneAuditEventKind
    {
        Split,
        Close,
        FocusChanged,
        Equalized,
        ZoomToggled,
        BroadcastToggled
    }

    public sealed class PaneAuditEvent
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public PaneAuditEventKind Kind { get; init; }
        public Guid TabId { get; init; }
        public Guid? PaneId { get; init; }
        public string Details { get; init; } = string.Empty;
    }

    public static class PaneEventStream
    {
        private const int MaxEvents = 4096;
        private static readonly ConcurrentQueue<PaneAuditEvent> _events = new();

        public static event Action<PaneAuditEvent>? EventPublished;

        public static void Publish(PaneAuditEvent evt)
        {
            _events.Enqueue(evt);
            while (_events.Count > MaxEvents && _events.TryDequeue(out _)) { }
            EventPublished?.Invoke(evt);
        }

        public static IReadOnlyList<PaneAuditEvent> Snapshot()
        {
            return _events.ToArray().ToList();
        }
    }
}
