using System;
using System.Threading;
using NovaTerminal.VT;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// The debug log's destination. A thin static front for <see cref="RotatingFileLogWriter"/>,
    /// which owns the mechanism — and, being constructible with its own path and limits, the tests.
    /// </summary>
    public static class AppLogger
    {
        private static readonly string LogFilePath = AppPaths.DebugLogPath;

        /// <summary>Rotate once the live file passes this. One previous generation is kept.</summary>
        private const long MaxBytes = 16L * 1024 * 1024;

        /// <summary>At ~100 bytes a line this caps the hand-off queue at a megabyte or so.</summary>
        private const int MaxQueuedMessages = 8192;

        private static readonly RotatingFileLogWriter Writer =
            new(LogFilePath, MaxBytes, MaxQueuedMessages);

        private static int _initialized;

        /// <summary>
        /// Attaches this sink to <see cref="TerminalLogger"/> and arms the shutdown drain. Call it
        /// once, before the first message worth keeping. Idempotent.
        /// </summary>
        /// <remarks>
        /// Explicit rather than a static constructor, because the wiring used to happen as a side
        /// effect of whoever touched the type first — and in <c>Program.Main</c> that was
        /// <c>AppLogger.GetLogFilePath()</c> on the <em>second</em> startup log line, so the first
        /// one ("NovaTerminal started with args: …") reached a <see cref="TerminalLogger.OnLog"/>
        /// that still had no subscriber and vanished. Initialization order that matters should be
        /// stated, not inferred from which member someone happens to touch first.
        /// </remarks>
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            TerminalLogger.OnLog += Log;

            // A background thread is killed mid-buffer at shutdown, which would lose exactly the
            // lines a crash report needs. Disposing the writer drains and flushes first.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Writer.Dispose();

            Log($"=== NovaTerminal Debug Log Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }

        /// <summary>
        /// Queues one line. Never blocks and never throws — this is reached from the PTY read
        /// thread and the render thread.
        /// </summary>
        public static void Log(string message) => Writer.Write(message);

        public static string GetLogFilePath() => LogFilePath;
    }
}
