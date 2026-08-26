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

            // Whole string first, so a path that simply contains spaces is found as itself rather
            // than mistaken for a command plus arguments. This is the same order
            // TerminalPane.InitializeSessionCore uses when it decides whether to split.
            if (CanExecute(probe) || InPath(probe))
            {
                return trimmed;
            }

            // A stored command may carry its arguments inline - "zsh -l", "wsl.exe -e /bin/bash" -
            // and the spawn path supports that, splitting executable from arguments. Probing the
            // whole string as one filename therefore rejected commands that run perfectly well, and
            // rejection costs the arguments too. Probe just the executable, and hand back the
            // combined string: the consumer splits it again with this same method, so the verdict
            // reached here and the command actually spawned cannot disagree.
            if (TrySplitCommandLine(trimmed, out string executable, out _) &&
                (CanExecute(executable) || InPath(executable)))
            {
                return trimmed;
            }

            return GetDefaultShell();
        }

        /// <summary>
        /// Splits a command that carries its arguments inline into its executable and argument
        /// parts, removing the quotes around a quoted executable.
        /// </summary>
        /// <remarks>
        /// The single definition of that split, used both by <see cref="ResolveExecutableOrDefault"/>
        /// to decide what to probe and by <c>TerminalPane.InitializeSessionCore</c> to decide what
        /// to spawn. They used to answer it separately and disagree: this validated the executable
        /// inside <c>"C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo</c> and approved the command,
        /// while the pane split the same string at the first literal space and tried to spawn
        /// <c>"C:\Program</c>. Approving a command nobody can launch is worse than rejecting it,
        /// because rejection at least falls back to a working shell.
        ///
        /// Returns false when there is nothing to split, leaving the whole string as the command.
        /// </remarks>
        public static bool TrySplitCommandLine(string? commandLine, out string executable, out string arguments)
        {
            executable = string.Empty;
            arguments = string.Empty;

            if (string.IsNullOrWhiteSpace(commandLine)) return false;

            string value = commandLine.Trim();

            // A quoted executable, with or without arguments after it:
            // "C:\Program Files\...\pwsh.exe" -NoLogo. The closing quote delimits it, not the first
            // space - which on Windows usually falls inside the path.
            if (value.StartsWith('"'))
            {
                int closingQuote = value.IndexOf('"', 1);
                if (closingQuote <= 1) return false;

                executable = value[1..closingQuote];
                arguments = value[(closingQuote + 1)..].Trim();
                return true;
            }

            // An existing file is itself, spaces and all. Splitting "/opt/my shell" would look for
            // "/opt/my" and pass "shell" as an argument.
            if (File.Exists(value)) return false;

            int firstSpace = value.IndexOf(' ');
            if (firstSpace <= 0) return false;

            executable = value[..firstSpace];
            arguments = value[(firstSpace + 1)..].Trim();
            return true;
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
        /// Extensions this application can actually start on Windows.
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>PATHEXT</c>. PATHEXT is the list a *shell* appends, and it includes
        /// script types - .BAT, .CMD, .PS1, .VBS - that only run because cmd.exe or another
        /// interpreter runs them. NovaTerminal does not launch through a shell: the native layer
        /// calls <c>CreateProcessW</c> directly, and the fallback hands the command to
        /// portable_pty's <c>CommandBuilder</c>. Neither can start a batch file without an
        /// interpreter.
        ///
        /// Probing the full PATHEXT therefore reported a .bat-backed command as runnable, and
        /// "runnable" is not an idle verdict here - it makes the caller keep the command and its
        /// arguments instead of falling back, so the user got a pane that failed to spawn rather
        /// than a working shell. Modelling cmd.exe was the wrong model; this models the launcher.
        /// </remarks>
        /// <remarks>
        /// <c>internal</c> so the policy itself can be asserted off Windows. The probe that uses it
        /// is gated on <see cref="OperatingSystem.IsWindows"/> and so does nothing on Linux, which
        /// means a test that goes through the filesystem there passes whatever this list contains -
        /// it would not have caught .bat being in it. The list is the decision worth pinning.
        /// </remarks>
        internal static readonly string[] LaunchableWindowsExtensions = { ".exe", ".com" };

        /// <summary>
        /// True when <paramref name="candidate"/> names a file this application could start,
        /// allowing for the extension being left off on Windows.
        /// </summary>
        private static bool CanExecute(string candidate)
        {
            if (File.Exists(candidate)) return true;
            if (!OperatingSystem.IsWindows()) return false;

            // Tried even when the name looks like it already has an extension: a "." in the stem
            // (python3.11) is not an executable extension, and the launcher would still go on to
            // append one. Guessing wrong here reintroduces the false negative, and an extra
            // File.Exists that misses costs nothing.
            foreach (string extension in LaunchableWindowsExtensions)
            {
                if (File.Exists(candidate + extension)) return true;
            }

            return false;
        }

        /// <summary>Strips one layer of surrounding double quotes.</summary>
        private static string Unquote(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1]
                : value;
    }
}
