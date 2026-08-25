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
        ///
        /// Be careful changing what counts as runnable: saying "no" here does not just pick a
        /// different command, it also makes <c>SessionManager</c> drop the pane's arguments, on the
        /// grounds that they belonged to the command being replaced. A false negative therefore
        /// costs the user their command *and* its arguments, silently.
        /// </remarks>
        public static string ResolveExecutableOrDefault(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return GetDefaultShell();
            }

            string trimmed = command.Trim();

            // Probed unquoted, returned as written: a configured command may be quoted to survive
            // spaces ("C:\Program Files\...\pwsh.exe"), which no File.Exists would ever match, and
            // whoever spawns it wants the original spelling back.
            string probe = Unquote(trimmed);

            return CanExecute(probe) || InPath(probe) ? trimmed : GetDefaultShell();
        }

        /// <summary>
        /// True when <paramref name="command"/> can be found on <c>PATH</c>.
        /// </summary>
        /// <remarks>
        /// Windows resolution is extension-aware. A command is legitimately written without one
        /// there - <c>pwsh</c> runs <c>pwsh.exe</c> - because the process launcher appends each
        /// <c>PATHEXT</c> entry when the name carries no extension of its own. Probing only the
        /// literal filename reported those as missing, which for <see cref="ResolveExecutableOrDefault"/>
        /// meant quietly replacing a working <c>pwsh</c>, <c>powershell</c> or <c>cmd</c> with the
        /// default shell and discarding its arguments.
        /// </remarks>
        public static bool InPath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return false;

            var dirs = path.Split(Path.PathSeparator);

            foreach (var dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                string fullPath;
                try
                {
                    fullPath = Path.Combine(dir, command);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing the whole probe over.
                    continue;
                }

                if (CanExecute(fullPath)) return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> names a file that exists, allowing for Windows
        /// appending a <c>PATHEXT</c> extension to a name that has none.
        /// </summary>
        private static bool CanExecute(string candidate)
        {
            if (File.Exists(candidate)) return true;
            if (!OperatingSystem.IsWindows()) return false;

            // Tried even when the name looks like it already has an extension: a "." in the stem
            // (python3.11) is not an executable extension, and the launcher would still go on to
            // append one. Guessing wrong here reintroduces the false negative, and an extra
            // File.Exists that misses costs nothing.
            foreach (string extension in WindowsExecutableExtensions())
            {
                if (File.Exists(candidate + extension)) return true;
            }

            return false;
        }

        /// <summary>
        /// The extensions Windows appends to an extensionless command, from <c>PATHEXT</c>.
        /// </summary>
        private static string[] WindowsExecutableExtensions() =>
            ParseExecutableExtensions(Environment.GetEnvironmentVariable("PATHEXT"));

        /// <summary>
        /// Parses a <c>PATHEXT</c> value into normalised extensions.
        /// </summary>
        /// <remarks>
        /// Split out as a pure function, and <c>internal</c> rather than private, so the parsing
        /// can be tested off Windows. The rest of the extension handling is a File.Exists loop,
        /// but this part has the details worth getting wrong - a cleared variable, entries without
        /// a leading dot, stray whitespace - and it is the half that does not need a Windows
        /// filesystem to verify. Taking the value as an argument rather than reading the
        /// environment also keeps the tests from mutating process-wide state other tests read.
        /// </remarks>
        internal static string[] ParseExecutableExtensions(string? pathExtValue)
        {
            // The launcher's own defaults, for the rare environment that clears PATHEXT.
            string pathExt = string.IsNullOrWhiteSpace(pathExtValue) ? ".COM;.EXE;.BAT;.CMD" : pathExtValue;

            string[] parts = pathExt.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                if (!parts[i].StartsWith('.')) parts[i] = "." + parts[i];
            }

            return parts;
        }

        /// <summary>Strips one layer of surrounding double quotes.</summary>
        private static string Unquote(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1]
                : value;
    }
}
