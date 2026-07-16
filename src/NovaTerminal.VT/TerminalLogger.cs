using System;

namespace NovaTerminal.VT
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
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        public static void Log(string message) => Log(LogLevel.Info, message);

        public static void Log(LogLevel level, string message)
        {
            if (level < MinimumLevel) return;

            // A throwing log hook must never disrupt callers — TerminalLogger is invoked from
            // critical recovery paths (e.g. Reflow's failsafe catch, the render catch) where an
            // escaping exception would skip the recovery that follows and could hang the terminal.
            try
            {
                if (OnLogLevel is { } structured)
                {
                    structured(level, message);
                    return;
                }

                OnLog?.Invoke(level == LogLevel.Info ? message : $"[{level}] {message}");
            }
            catch
            {
                // Swallow: logging failures must not propagate into error-handling paths.
            }
        }

        public static void Debug(string message) => Log(LogLevel.Debug, message);
        public static void Info(string message) => Log(LogLevel.Info, message);
        public static void Warning(string message) => Log(LogLevel.Warning, message);
        public static void Error(string message) => Log(LogLevel.Error, message);
    }
}
