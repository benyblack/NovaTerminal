using System;
using System.IO;

namespace NovaTerminal.Core
{
    public static class AppLogger
    {
        private static readonly string LogFilePath = AppPaths.DebugLogPath;

        static AppLogger()
        {
            TerminalLogger.OnLog += Log;
            AppPaths.EnsureInitialized();

            // Ensure the directory exists
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Clear the log file at startup
            File.WriteAllText(LogFilePath, $"=== NovaTerminal Debug Log Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }

        private static readonly object _lock = new object();
        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
                    File.AppendAllText(LogFilePath, logEntry);
                }
            }
            catch
            {
                // If logging fails, we don't want it to break the application
            }
        }

        public static string GetLogFilePath()
        {
            return LogFilePath;
        }
    }
}
