using System;
using System.IO;
using System.Linq;

namespace NovaTerminal.Core
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

        private static bool InPath(string command)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return false;

            var ext = ".exe";
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
