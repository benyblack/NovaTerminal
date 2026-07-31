using System;

namespace NovaTerminal.Pty
{
    /// <summary>Severity of a <see cref="PtyLogger"/> message. Mirrors the app's log levels.</summary>
    public enum PtyLogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Diagnostic sink for the PTY layer.
    /// </summary>
    /// <remarks>
    /// #109: this layer's diagnostics used to go to <c>Console.WriteLine</c>. In a Windows GUI process
    /// there is no console attached, so every one of those messages — spawn parameters, read-loop
    /// failures, join timeouts, lost input on a short write — was written to nothing. They looked like
    /// logging and were closer to comments.
    ///
    /// This is deliberately not <c>NovaTerminal.VT.TerminalLogger</c>, even though it duplicates a
    /// little of its shape. <c>Pty_must_not_depend_on_Vt</c> in the architecture tests forbids it at IL
    /// level (VT is reachable transitively through Replay, so it would have compiled), and the PTY layer
    /// genuinely should not need the terminal emulator to report that a pipe read failed. The App wires
    /// <see cref="Sink"/> to <c>TerminalLogger</c> at startup, so messages land in the same debug log as
    /// everything else.
    ///
    /// The honest longer-term fix is to move the logging facility out of VT into a leaf both layers can
    /// reference; recorded on #109 rather than done here, because relocating a public type used
    /// repo-wide does not belong in the change that stops dropping messages.
    /// </remarks>
    public static class PtyLogger
    {
        /// <summary>Receives every message at or above <see cref="MinimumLevel"/>. Null discards.</summary>
        public static Action<PtyLogLevel, string>? Sink { get; set; }

        public static PtyLogLevel MinimumLevel { get; set; } = PtyLogLevel.Debug;

        public static void Log(PtyLogLevel level, string message)
        {
            if (level < MinimumLevel) return;

            // A throwing sink must never disrupt the caller: these calls sit inside teardown paths and
            // catch blocks, where an escaping exception would skip the recovery that follows. Same
            // reasoning as TerminalLogger.Log.
            try
            {
                Sink?.Invoke(level, message);
            }
            catch
            {
                // Swallow: logging failures must not propagate into error-handling paths.
            }
        }

        public static void Debug(string message) => Log(PtyLogLevel.Debug, message);
        public static void Info(string message) => Log(PtyLogLevel.Info, message);
        public static void Warning(string message) => Log(PtyLogLevel.Warning, message);
        public static void Error(string message) => Log(PtyLogLevel.Error, message);
    }
}
