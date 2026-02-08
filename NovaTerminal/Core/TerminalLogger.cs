using System;
using System.IO;

namespace NovaTerminal.Core
{
    public static class TerminalLogger
    {
        private static readonly string LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaTerminal", "debug.log");
        
        static TerminalLogger()
        {
            // Ensure the directory exists
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Clear the log file at startup
            File.WriteAllText(LogFilePath, $"=== NovaTerminal Debug Log Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
        
        public static void Log(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
                File.AppendAllText(LogFilePath, logEntry);
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