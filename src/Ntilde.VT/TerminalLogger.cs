using System;
using System.Collections.Concurrent;
using System.Text;

namespace Ntilde.VT
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public static class TerminalLogger
    {
        // Existing unstructured hook — kept so current consumers (and Log(string)) are unchanged.
        public static Action<string>? OnLog { get; set; }

        // Optional structured hook. When set, it receives the level; when unset, leveled messages
        // fall back to OnLog with a "[LEVEL] " prefix (Info passes through verbatim, as before).
        public static Action<LogLevel, string>? OnLogLevel { get; set; }

        // Messages below this level are dropped before any hook is invoked.
        //
        // Debug by default so a library consumer (and the test suite) sees everything unless it
        // says otherwise; the app narrows this at startup from NTILDE_LOG_LEVEL. The default
        // is the permissive one because a missing diagnostic is the failure people report, and
        // the hosts that cannot afford one are the hosts that know it.
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        /// <summary>
        /// Emissions of one message shape before the shape is muted, or 0 to disable muting.
        /// </summary>
        /// <remarks>
        /// The durable half of the fix for the unbounded debug log. Demoting today's chatty call
        /// sites to <see cref="LogLevel.Debug"/> stops today's flood, but the flood was never
        /// really about which sequences the parser happens not to implement: it was about a sink
        /// that writes a line per event, from the parse thread, for as long as the process lives.
        /// The next unimplemented sequence — or any per-frame diagnostic somebody adds above Debug
        /// — would reproduce it exactly.
        ///
        /// So repetition is capped here, in front of every call site, rather than trusted to each
        /// one. A shape that has been muted still reports itself: <see cref="MuteRecheckInterval"/>
        /// later it emits once more, carrying the number of occurrences it swallowed, so a
        /// repeating problem stays visible in the log without being the log.
        /// </remarks>
        public static int MaxRepeatsPerShape { get; set; } = 20;

        /// <summary>How long a muted shape stays muted before it reports its backlog and re-arms.</summary>
        public static TimeSpan MuteRecheckInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Ceiling on tracked shapes, so a stream that manufactures unique shapes cannot turn the
        /// rate limiter into the leak it exists to prevent. On overflow the table is cleared
        /// wholesale rather than evicted one entry at a time — losing the counts degrades muting
        /// for old shapes, and a stream pathological enough to reach the cap has no shape worth
        /// preserving. Same reasoning, and the same shape, as HyperlinkRegistry's interning cap.
        /// </summary>
        private const int MaxTrackedShapes = 1024;

        private sealed class ShapeState
        {
            public int Count;
            public int SuppressedSinceReport;
            public long MutedAtTicks;
        }

        private static readonly ConcurrentDictionary<string, ShapeState> Shapes = new(StringComparer.Ordinal);

        /// <summary>Drops every tracked shape, so muting starts over. For tests and for a host
        /// that wants a clean slate (a new session, a reopened log).</summary>
        public static void ResetRateLimiter() => Shapes.Clear();

        public static void Log(string message) => Log(LogLevel.Info, message);

        public static void Log(LogLevel level, string message)
        {
            if (level < MinimumLevel) return;

            // A throwing log hook must never disrupt callers — TerminalLogger is invoked from
            // critical recovery paths (e.g. Reflow's failsafe catch, the render catch) where an
            // escaping exception would skip the recovery that follows and could hang the terminal.
            try
            {
                if (!TryAdmit(level, message, out string? suffix))
                {
                    return;
                }

                string emitted = suffix == null ? message : message + suffix;

                if (OnLogLevel is { } structured)
                {
                    structured(level, emitted);
                    return;
                }

                OnLog?.Invoke(level == LogLevel.Info ? emitted : $"[{level}] {emitted}");
            }
            catch
            {
                // Swallow: logging failures must not propagate into error-handling paths.
            }
        }

        /// <summary>
        /// Decides whether this message is emitted, and hands back the note to append when it is
        /// the one that breaks a mute.
        /// </summary>
        private static bool TryAdmit(LogLevel level, string message, out string? suffix)
        {
            suffix = null;

            int cap = MaxRepeatsPerShape;
            if (cap <= 0)
            {
                return true;
            }

            // Errors are never muted. They are rare by construction, and an error that stops being
            // written because it happened too often is the one case where the mute would hide
            // exactly what the log exists to capture.
            if (level >= LogLevel.Error)
            {
                return true;
            }

            if (Shapes.Count >= MaxTrackedShapes)
            {
                Shapes.Clear();
            }

            var state = Shapes.GetOrAdd(ShapeOf(message), static _ => new ShapeState());

            lock (state)
            {
                if (state.Count < cap)
                {
                    state.Count++;
                    return true;
                }

                long now = Environment.TickCount64;
                if (state.MutedAtTicks == 0)
                {
                    state.MutedAtTicks = now;
                    state.SuppressedSinceReport++;
                    suffix = " [further occurrences muted]";
                    return true;
                }

                if (now - state.MutedAtTicks < (long)MuteRecheckInterval.TotalMilliseconds)
                {
                    state.SuppressedSinceReport++;
                    return false;
                }

                int swallowed = state.SuppressedSinceReport;
                state.SuppressedSinceReport = 0;
                state.MutedAtTicks = now;
                suffix = $" [{swallowed} further occurrences muted since the last report]";
                return true;
            }
        }

        /// <summary>
        /// Collapses a message to the shape it shares with its repeats: runs of digits become a
        /// single '#'.
        /// </summary>
        /// <remarks>
        /// Automatic rather than a key each call site passes, because the call sites are the thing
        /// that cannot be trusted to remember — there are ninety of them and the next one is not
        /// written yet. Digits are what vary between repeats of one diagnostic ("Unhandled CSI: b
        /// … params=39" and "… params=60" are one problem, reported twice), and collapsing them
        /// costs one pass over a string that has already passed the level filter, so it never runs
        /// on the hot path at the app's configured level.
        ///
        /// It over-collapses in principle — two genuinely different messages that differ only in a
        /// number share a shape, and the second is muted after the first has been seen 20 times.
        /// That is the right direction: the cost is a diagnostic that says "20 of these, plus a
        /// count" instead of thousands of lines, and the count is still there.
        /// </remarks>
        internal static string ShapeOf(string message)
        {
            bool hasDigit = false;
            for (int i = 0; i < message.Length; i++)
            {
                if (char.IsAsciiDigit(message[i])) { hasDigit = true; break; }
            }

            if (!hasDigit)
            {
                return message;
            }

            var sb = new StringBuilder(message.Length);
            bool inRun = false;
            foreach (char c in message)
            {
                if (char.IsAsciiDigit(c))
                {
                    if (!inRun) { sb.Append('#'); inRun = true; }
                }
                else
                {
                    sb.Append(c);
                    inRun = false;
                }
            }

            return sb.ToString();
        }

        public static void Debug(string message) => Log(LogLevel.Debug, message);
        public static void Info(string message) => Log(LogLevel.Info, message);
        public static void Warning(string message) => Log(LogLevel.Warning, message);
        public static void Error(string message) => Log(LogLevel.Error, message);
    }
}
