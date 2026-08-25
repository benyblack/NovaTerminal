using System;
using System.IO;
using System.Linq;

namespace NovaTerminal.Pty
{
    public static class ShellHelper
    {
        public static string GetDefaultShell()
        {
            if (OperatingSystem.IsWindows())
            {
                string[] shells = { "pwsh.exe", "powershell.exe", "cmd.exe" };
                foreach (var shell in shells)
                {
                    if (InPath(shell)) return shell;
                }
                return "cmd.exe";
            }
            else
            {
                string[] shells = { "/bin/zsh", "/bin/bash", "/bin/sh" };
                foreach (var shell in shells)
                {
                    if (File.Exists(shell)) return shell;
                }
                return "/bin/sh";
            }
        }

        /// <summary>
        /// Resolves a configured or persisted local shell command to one that can actually be
        /// spawned here, falling back to <see cref="GetDefaultShell"/> when it cannot.
        /// </summary>
        /// <remarks>
        /// Settings and session files travel between machines, and a pane's command is persisted
        /// as a bare executable, so a workspace saved on Windows restores as <c>cmd.exe</c> on
        /// Linux and every pane in it fails to spawn. Callers used to guard only against a blank
        /// command, which does not catch that: <c>cmd.exe</c> is a perfectly non-blank string.
        ///
        /// This mirrors the existence check the profile-launch path in <c>MainWindow</c> already
        /// applies to local profiles, so a restored pane and a profile-launched one agree about
        /// what is runnable.
        /// </remarks>
        public static string ResolveExecutableOrDefault(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return GetDefaultShell();
            }

            string trimmed = command.Trim();
            return File.Exists(trimmed) || InPath(trimmed) ? trimmed : GetDefaultShell();
        }

        public static bool InPath(string command)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return false;

            var dirs = path.Split(Path.PathSeparator);

            foreach (var dir in dirs)
            {
                var fullPath = Path.Combine(dir, command);
                if (File.Exists(fullPath)) return true;
            }
            return false;
        }
    }
}
