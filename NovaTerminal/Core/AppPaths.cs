using System;
using System.IO;

namespace NovaTerminal.Core
{
    public static class AppPaths
    {
        private const string AppName = "NovaTerminal";
        private static readonly object InitLock = new();
        private static bool _initialized;

        static AppPaths()
        {
            EnsureInitialized();
        }

        public static string RootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);

        public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");
        public static string ThemesDirectory => Path.Combine(RootDirectory, "themes");
        public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
        public static string DebugLogPath => Path.Combine(LogsDirectory, "debug.log");
        public static string StartupErrorFilePath => Path.Combine(LogsDirectory, "startup_error.txt");
        public static string ResizeDebugLogPath => Path.Combine(LogsDirectory, "resize_debug.log");
        public static string SessionsDirectory => Path.Combine(RootDirectory, "sessions");
        public static string SessionFilePath => Path.Combine(SessionsDirectory, "last_session.json");
        public static string RecordingsDirectory => Path.Combine(RootDirectory, "recordings");

        public static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (InitLock)
            {
                if (_initialized) return;

                try
                {
                    Directory.CreateDirectory(RootDirectory);
                    Directory.CreateDirectory(ThemesDirectory);
                    Directory.CreateDirectory(LogsDirectory);
                    Directory.CreateDirectory(SessionsDirectory);
                    Directory.CreateDirectory(RecordingsDirectory);

                    string legacyBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string legacyRoamingRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        AppName);

                    MigrateFileIfNeeded(Path.Combine(legacyBaseDir, "settings.json"), SettingsFilePath);
                    MigrateDirectoryIfNeeded(Path.Combine(legacyBaseDir, "themes"), ThemesDirectory);
                    MigrateFileIfNeeded(Path.Combine(legacyRoamingRoot, "debug.log"), DebugLogPath);
                    MigrateFileIfNeeded(Path.Combine(legacyRoamingRoot, "last_session.json"), SessionFilePath);
                    MigrateFileIfNeeded(Path.Combine(legacyBaseDir, "startup_error.txt"), StartupErrorFilePath);
                    MigrateDirectoryIfNeeded(Path.Combine(legacyBaseDir, "recordings"), RecordingsDirectory);
                }
                catch
                {
                    // Keep path init best-effort; callers should remain resilient if storage is unavailable.
                }

                _initialized = true;
            }
        }

        public static void MigrateFileIfNeeded(string sourcePath, string destinationPath)
        {
            try
            {
                if (!File.Exists(sourcePath)) return;

                string sourceFullPath = Path.GetFullPath(sourcePath);
                string destinationFullPath = Path.GetFullPath(destinationPath);
                if (PathsEqual(sourceFullPath, destinationFullPath)) return;

                string? destinationDirectory = Path.GetDirectoryName(destinationFullPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (File.Exists(destinationFullPath))
                {
                    DateTime sourceWrite = File.GetLastWriteTimeUtc(sourceFullPath);
                    DateTime destinationWrite = File.GetLastWriteTimeUtc(destinationFullPath);
                    if (destinationWrite >= sourceWrite) return;
                }

                File.Copy(sourceFullPath, destinationFullPath, overwrite: true);
            }
            catch
            {
                // Best-effort migration only.
            }
        }

        public static void MigrateDirectoryIfNeeded(string sourceDirectory, string destinationDirectory)
        {
            try
            {
                if (!Directory.Exists(sourceDirectory)) return;

                string sourceFullPath = Path.GetFullPath(sourceDirectory);
                string destinationFullPath = Path.GetFullPath(destinationDirectory);
                if (PathsEqual(sourceFullPath, destinationFullPath)) return;

                Directory.CreateDirectory(destinationFullPath);

                foreach (string sourceFile in Directory.GetFiles(sourceFullPath, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(sourceFullPath, sourceFile);
                    string destinationFile = Path.Combine(destinationFullPath, relativePath);
                    MigrateFileIfNeeded(sourceFile, destinationFile);
                }
            }
            catch
            {
                // Best-effort migration only.
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return OperatingSystem.IsWindows()
                ? string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                : string.Equals(left, right, StringComparison.Ordinal);
        }
    }
}
