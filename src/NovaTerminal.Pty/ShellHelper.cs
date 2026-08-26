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
        /// <param name="workingDirectory">
        /// The directory the command will be launched in, when the caller knows it - a profile's
        /// StartingDirectory. A relative path is resolved against it as well as against this
        /// process's own directory, because the child gets that directory as its cwd.
        /// </param>
        public static string ResolveExecutableOrDefault(string? command, string? workingDirectory = null)
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
            if (IsRunnable(probe, workingDirectory))
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
                IsRunnable(executable, workingDirectory))
            {
                return trimmed;
            }

            return GetDefaultShell();
        }

        /// <summary>
        /// True when <paramref name="executable"/> names something this application could start,
        /// looked for as a path, on <c>PATH</c>, and relative to <paramref name="workingDirectory"/>.
        /// </summary>
        private static bool IsRunnable(string executable, string? workingDirectory) =>
            CanExecute(executable) ||
            InPath(executable) ||
            CanExecuteRelativeTo(workingDirectory, executable);

        /// <summary>
        /// True when <paramref name="candidate"/> resolves against the directory the command will
        /// actually run in.
        /// </summary>
        /// <remarks>
        /// A profile's StartingDirectory becomes the child's cwd (TerminalPane passes it to
        /// RustPtySession), so a relative command such as <c>./tools/shell</c> runs from there -
        /// while this check runs in NovaTerminal's own directory and used to declare it missing,
        /// substituting the default shell and dropping the arguments.
        ///
        /// Unix only, and that asymmetry is the whole point. <c>exec</c> resolves a relative path
        /// after the child has changed directory, so the working directory is where it is found.
        /// <c>CreateProcessW</c> resolves its application name against the *calling* process's
        /// directory; <c>lpCurrentDirectory</c> only becomes the child's cwd and plays no part in
        /// finding the executable. So on Windows a match here means nothing - approving the command
        /// on that basis keeps it, and its arguments, for a spawn that then fails.
        ///
        /// An earlier version probed both platforms, reasoning that being additive could only avoid
        /// false negatives. That was wrong in one direction: on Windows it manufactures a false
        /// positive, which is the more expensive mistake, since rejection at least falls back to a
        /// shell that starts. The remark above it described the Windows rule correctly and the code
        /// beneath it did the opposite.
        ///
        /// Only for something that is already a path. A bare name like <c>zsh</c> is resolved
        /// through PATH, not the working directory, which is what both a shell and exec do.
        /// </remarks>
        private static bool CanExecuteRelativeTo(string? workingDirectory, string candidate)
        {
            if (OperatingSystem.IsWindows()) return false;
            if (string.IsNullOrWhiteSpace(workingDirectory)) return false;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (Path.IsPathRooted(candidate)) return false;
            if (candidate.IndexOf('/') < 0 && candidate.IndexOf('\\') < 0) return false;

            try
            {
                return CanExecute(Path.Combine(workingDirectory, candidate));
            }
            catch (ArgumentException)
            {
                return false;
            }
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
        /// One entry, and specifically <c>.exe</c>, because that is the only extension the launcher
        /// appends. The native path calls <c>CreateProcessW</c> with <c>lpApplicationName</c> NULL
        /// (native/src/lib.rs:301), and Windows then appends <c>.exe</c> - not <c>.com</c>, and not
        /// anything from PATHEXT. Measured on Windows: an extensionless name backed only by a
        /// <c>.com</c> fails with ERROR_FILE_NOT_FOUND, while the same file named in full succeeds.
        ///
        /// The two launch paths disagree, so this models the stricter one. portable_pty's
        /// <c>CommandBuilder</c> walks the full PATHEXT when it searches (cmdbuilder.rs), so it is
        /// *more* permissive than CreateProcessW, not equally strict - an earlier version of this
        /// remark claimed neither could manage it, which was wrong about portable_pty.
        ///
        /// <c>internal</c> so the policy itself can be asserted off Windows. The probe that uses it
        /// is gated on <see cref="OperatingSystem.IsWindows"/> and so does nothing on Linux, which
        /// means a test that goes through the filesystem there passes whatever this list contains -
        /// it would not have caught .bat being in it. The list is the decision worth pinning.
        /// </remarks>
        internal static readonly string[] LaunchableWindowsExtensions = { ".exe" };

        /// <summary>
        /// Windows file types that only run because an interpreter runs them, so this application
        /// cannot start one directly.
        /// </summary>
        /// <remarks>
        /// Deliberately excludes <c>.bat</c> and <c>.cmd</c>, which this list used to contain on a
        /// premise that turned out to be false. <c>CreateProcess</c> special-cases batch files and
        /// runs them through the command processor, so they start perfectly well. Measured on
        /// Windows against a raw <c>CreateProcessW</c> P/Invoke matching native/src/lib.rs, and again
        /// through <c>RustPtySession</c> itself: a full-path <c>.bat</c> and <c>.cmd</c> each spawned
        /// with a live pid, and the <c>.bat</c> produced its own output. Rejecting them substituted
        /// the default shell for a wrapper that had been working, losing the command and its
        /// arguments - the expensive failure this check is supposed to prevent.
        ///
        /// What remains are the types that genuinely need an interpreter: <c>.ps1</c>, <c>.vbs</c> and
        /// friends fail with ERROR_BAD_EXE_FORMAT (193) from the same call.
        ///
        /// The counterpart to <see cref="LaunchableWindowsExtensions"/>: that list is what the
        /// launcher will *append* to a bare name, this one is what it cannot start even when named in
        /// full. They are separate questions and a name being spelled out does not make an
        /// unstartable file startable.
        /// </remarks>
        internal static readonly string[] WindowsExtensionsNeedingAnInterpreter =
            { ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msc" };

        /// <summary>
        /// True when <paramref name="candidate"/> names a file this application could start,
        /// allowing for the extension being left off on Windows.
        /// </summary>
        private static bool CanExecute(string candidate)
        {
            // Checked before File.Exists, not after: the file existing is exactly what used to make
            // an explicitly named script look runnable.
            if (OperatingSystem.IsWindows() && NeedsAnInterpreter(candidate)) return false;

            if (!OperatingSystem.IsWindows())
            {
                // Existing is not the same as spawnable here. A regular file with no execute bit -
                // a config file, a Windows .exe sitting in a shared workspace, a script saved
                // without chmod +x - passes File.Exists and then fails at exec, which is the dead
                // pane this check exists to avoid.
                return HasAnExecuteBit(candidate);
            }

            if (File.Exists(candidate)) return true;
            if (!AppendsImplicitExtension(candidate)) return false;

            foreach (string extension in LaunchableWindowsExtensions)
            {
                if (File.Exists(candidate + extension)) return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is a file with an execute bit set.
        /// </summary>
        /// <remarks>
        /// Any of user/group/other, rather than resolving effective access for the current user.
        /// That over-approximates slightly - a file executable only by someone else counts - but it
        /// rejects the case that actually occurs, a file carrying no execute bit at all, and erring
        /// toward "runnable" here only preserves today's behaviour. Erring the other way would
        /// replace a working command with a shell.
        /// </remarks>
        private static bool HasAnExecuteBit(string candidate)
        {
            if (!File.Exists(candidate)) return false;

            try
            {
                UnixFileMode mode = File.GetUnixFileMode(candidate);
                const UnixFileMode anyExecute =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

                return (mode & anyExecute) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Cannot read the mode: fall back to existence rather than declaring a command
                // missing on the strength of a failed stat.
                return true;
            }
        }

        /// <summary>
        /// True when Windows would append an extension to <paramref name="candidate"/> while
        /// looking for the executable.
        /// </summary>
        /// <remarks>
        /// Only for a name with no extension at all. <c>CreateProcessW</c> with
        /// <c>lpApplicationName</c> NULL appends <c>.exe</c> when "the file name does not contain an
        /// extension", and a dot anywhere in the final component counts - so <c>python3.11</c> is
        /// treated as already extensioned and <c>python3.11.exe</c> is never found.
        ///
        /// This probe used to append regardless, on a comment of mine arguing that a dot in the stem
        /// is not a *real* extension so the launcher would keep looking. That describes what a shell
        /// does. It is the same cmd.exe-shaped assumption that first admitted .bat and .com, left
        /// standing in the adjacent decision after those were corrected.
        ///
        /// <c>internal</c> for the same reason as the extension lists: the caller is gated on
        /// Windows, so this cannot be reached from a filesystem test on Linux.
        /// </remarks>
        internal static bool AppendsImplicitExtension(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            return !Path.HasExtension(candidate);
        }

        /// <summary>
        /// True when <paramref name="candidate"/> ends in an extension that needs an interpreter.
        /// </summary>
        /// <remarks>
        /// <c>internal</c> for the same reason as the extension lists: the callers are gated on
        /// Windows, so a test that goes through the filesystem on Linux cannot exercise this.
        /// </remarks>
        internal static bool NeedsAnInterpreter(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            string extension = Path.GetExtension(candidate);
            if (extension.Length == 0) return false;

            foreach (string scripted in WindowsExtensionsNeedingAnInterpreter)
            {
                if (string.Equals(extension, scripted, StringComparison.OrdinalIgnoreCase)) return true;
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
