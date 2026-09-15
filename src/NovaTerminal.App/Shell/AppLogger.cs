using System;
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

        private static readonly RotatingFileLogWriter Writer;

        static AppLogger()
        {
            AppPaths.EnsureInitialized();
            Writer = new RotatingFileLogWriter(LogFilePath, MaxBytes, MaxQueuedMessages);

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
