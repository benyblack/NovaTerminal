using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace NovaTerminal.Pty
{
    public static class ShellHelper
    {
        /// <summary>
        /// This platform's shell for every "the user did not name one" path: new profiles,
        /// blank configured commands, session restore of a command written for another OS.
        /// On Unix that is the user's actual login shell — environment first, then the passwd
        /// database — not a preference order between whichever shells happen to be installed.
        /// </summary>
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

            return GetUnixDefaultShell(
                Environment.GetEnvironmentVariable,
                IsLaunchableShell,
                ReadLoginShellFromPasswd);
        }

        /// <summary>
        /// The Unix half of <see cref="GetDefaultShell"/>, as a resolution chain: $SHELL,
        /// then the passwd entry for the current user, then the old existence probe as the
        /// floor. The delegates exist so the chain is testable from any platform; production
        /// passes the real environment, filesystem and passwd reader.
        /// </summary>
        /// <remarks>
        /// $SHELL leads because it is what launchd sets from the login shell for GUI
        /// applications on macOS and what the desktop session sets on most Linux distros —
        /// the one place the login shell is already answered without re-deriving it. The
        /// passwd lookup covers the sessions that never got a $SHELL; macOS GUI users are
        /// absent from /etc/passwd (Directory Services owns them), so in practice that step
        /// mostly answers Linux. The old zsh/bash/sh probe stays as the floor so a machine
        /// where neither source answers behaves exactly as before.
        ///
        /// Every answer is probed with <paramref name="isLaunchable"/> before being trusted: a
        /// $SHELL left pointing at a since-uninstalled shell (brew remove, chsh followed by
        /// deletion) must fall through, not be handed to CreateProcess as a guaranteed
        /// launch, and neither may a file this account cannot actually execute (see
        /// <see cref="IsLaunchableShell"/>). It is the same conservatism as
        /// <see cref="ResolveExecutableOrDefault"/>: a shell we resolve is a shell we vouch for.
        ///
        /// The passwd step is a plain /etc/passwd read, so accounts served only through NSS
        /// (SSSD, LDAP, Active Directory) are invisible to it. Those sessions normally carry
        /// $SHELL from the login environment; when they do not, the floor below degrades to
        /// exactly the pre-login-shell behaviour rather than to something new. Deriving the
        /// answer through getpwuid instead would need a libc P/Invoke, which this cold a
        /// path is deliberately not paying for.
        /// </remarks>
        internal static string GetUnixDefaultShell(
            Func<string, string?> getEnvironmentVariable,
            Func<string, bool> isLaunchable,
            Func<string?> readLoginShellFromPasswd)
        {
            string? fromEnvironment = getEnvironmentVariable("SHELL");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && isLaunchable(fromEnvironment))
            {
                return fromEnvironment;
            }

            string? fromPasswd = readLoginShellFromPasswd();
            if (!string.IsNullOrWhiteSpace(fromPasswd) && isLaunchable(fromPasswd))
            {
                return fromPasswd;
            }

            string[] shells = { "/bin/zsh", "/bin/bash", "/bin/sh" };
            foreach (var shell in shells)
            {
                if (isLaunchable(shell)) return shell;
            }
            return "/bin/sh";
        }

        /// <summary>
        /// True when <paramref name="path"/> exists and this account can execute it.
        /// </summary>
        /// <remarks>
        /// File.Exists alone accepts a file whose mode has no x bit at all, and even an
        /// execute-bit check accepts a bit from a permission class that does not apply to
        /// this process - owner-only exec on a file the account does not own fails with
        /// EACCES all the same. So on Unix the verdict is <c>access(X_OK)</c>, which asks
        /// exactly the kernel's question for this uid and gid. That call is the primary
        /// probe, not the only tier: libc missing or unanswerable degrades to the mode
        /// heuristic, and unreadable mode metadata degrades further to the existence
        /// verdict. The PTY spawn remains the final arbiter either way.
        /// </remarks>
        internal static bool IsLaunchableShell(string path)
            => IsLaunchableShell(path, ExecProbeAnswersExecutable);

        internal static bool IsLaunchableShell(string path, Func<string, bool> execProbe)
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;

            try
            {
                return execProbe(path);
            }
            catch
            {
                // The native probe cannot answer on this platform. Fall back to the mode
                // heuristic; if even the mode is unreadable, trust the existence verdict.
                try { return HasAnyExecuteBit(File.GetUnixFileMode(path)); }
                catch { return true; }
            }
        }

        /// <summary>A mode heuristic, used only when <c>access</c> cannot answer.</summary>
        internal static bool HasAnyExecuteBit(UnixFileMode mode)
            => mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);

        private static bool ExecProbeAnswersExecutable(string path)
            => access(path, X_OK) == 0;

        private const int X_OK = 1;

        [DllImport("libc")]
        private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int mode);

        private static string? ReadLoginShellFromPasswd()
        {
            try
            {
                return ParseLoginShellFromPasswd(File.ReadAllLines("/etc/passwd"), Environment.UserName);
            }
            catch
            {
                // Unreadable is an expected shape, not a failure: macOS keeps its users in
                // Directory Services, and there is nothing to answer with. The floor probe
                // in GetUnixDefaultShell takes it from here.
                return null;
            }
        }

        /// <summary>
        /// The login shell (the seventh passwd field) for <paramref name="username"/>, or
        /// null when the file has no usable entry for that user.
        /// </summary>
        /// <remarks>
        /// Matched on the user name rather than the uid: the uid would need a getpwuid
        /// P/Invoke, and .NET already resolved <see cref="Environment.UserName"/> from the
        /// same account database the passwd file was generated from.
        /// </remarks>
        internal static string? ParseLoginShellFromPasswd(string[] lines, string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            foreach (string line in lines)
            {
                string[] fields = line.Split(':');
                if (fields.Length >= 7 &&
                    string.Equals(fields[0], username, StringComparison.Ordinal))
                {
                    return fields[6];
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a configured or persisted local shell command, substituting this platform's
        /// default shell when the command was written for a different operating system.
        /// </summary>
        /// <remarks>
        /// Settings and session files travel between machines. A workspace saved on Windows restores
        /// as <c>cmd.exe</c> on Linux, every pane in it fails to spawn, and the failing command is
        /// captured again on exit - so the terminal is dead on every subsequent launch and no amount
        /// of restarting clears it. That is the bug this exists for, and it is a portability
        /// question: the command is fine, it is just addressed to the wrong OS.
        ///
        /// It deliberately does not ask the broader question, "can this be launched here". An earlier
        /// version did, and grew to seven branches over as many review rounds - PATHEXT probing,
        /// implicit extension rules, an interpreter-extension list, execute bits, working-directory
        /// resolution - because the specification of that question is "match the real launcher
        /// exactly", which has no stopping point. Each branch was a chance to model the launcher
        /// wrongly and two of them did: batch files were rejected as unlaunchable when
        /// <c>CreateProcess</c> runs them perfectly well, and a relative path was approved against a
        /// directory Windows does not resolve it from. Both replaced a working command with a shell,
        /// silently.
        ///
        /// Which is the other half of the reasoning. Rejecting is not the cheap option it looks like:
        /// the user did not ask for *a* shell, they asked for theirs, and they get the substitute
        /// with no message. A pane that fails to spawn is visible and diagnosable; a pane quietly
        /// running something else is neither. So the bar for substituting is high, and a command that
        /// merely cannot be found is left alone to fail where the user can see it.
        ///
        /// Being simple enough to use everywhere is the point. The same predicate now backs the
        /// session-restore paths, the profile-launch path, the command palette and settings
        /// validation, which previously each ran their own existence check and disagreed - the same
        /// profile could be kept on restore, substituted on launch, and hidden from the palette.
        /// </remarks>
        public static string ResolveExecutableOrDefault(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return GetDefaultShell();
            }

            string trimmed = command.Trim();
            return IsCommandForAnotherPlatform(trimmed) ? GetDefaultShell() : trimmed;
        }

        /// <summary>
        /// True when <paramref name="command"/> names an executable belonging to a different
        /// operating system, and so could never start here.
        /// </summary>
        /// <remarks>
        /// Judged on the shape of the name alone - no filesystem access, no launcher emulation.
        /// Deliberately conservative: it fires only on signals that do not plausibly mean anything
        /// else, so a command it does not recognise is left alone rather than replaced. "Plausibly"
        /// is doing real work there - see <see cref="UnixFilesystemRoots"/>, where one entry is a
        /// probability judgement rather than an impossibility. Missing the occasional
        /// foreign command costs a spawn failure the user can read; a false positive silently swaps
        /// out a command that works.
        ///
        /// Arguments are ignored, via <see cref="TrySplitCommandLine"/>, so <c>cmd.exe /c build</c>
        /// is recognised by its executable. <c>pwsh</c> is intentionally absent from the Windows
        /// names: PowerShell is cross-platform and <c>pwsh</c> is a perfectly good Linux command.
        /// </remarks>
        public static bool IsCommandForAnotherPlatform(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            string executable = command.Trim();
            if (TrySplitCommandLine(executable, out string exePart, out _))
            {
                executable = exePart;
            }

            executable = Unquote(executable).Trim();
            if (executable.Length == 0) return false;

            return OperatingSystem.IsWindows()
                ? LooksLikeAUnixCommand(executable)
                : LooksLikeAWindowsCommand(executable);
        }

        /// <summary>Windows-only shapes: a drive-rooted path, a UNC path, a Windows executable
        /// suffix, or one of the shells that exist only there.</summary>
        private static bool LooksLikeAWindowsCommand(string executable)
        {
            // UNC, which needs both leading slashes: a single one is not a Windows-only shape.
            if (executable.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            if (executable.Length >= 3 &&
                char.IsAsciiLetter(executable[0]) &&
                executable[1] == ':' &&
                (executable[2] == '\\' || executable[2] == '/'))
            {
                return true;
            }

            string extension = Path.GetExtension(executable);
            foreach (string windowsOnly in WindowsExecutableSuffixes)
            {
                if (string.Equals(extension, windowsOnly, StringComparison.OrdinalIgnoreCase)) return true;
            }

            string name = executable.Replace('\\', '/');
            int lastSlash = name.LastIndexOf('/');
            if (lastSlash >= 0) name = name[(lastSlash + 1)..];

            foreach (string shell in WindowsOnlyShellNames)
            {
                if (string.Equals(name, shell, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>Unix-only shapes: a path under one of the filesystem roots only Unix has.</summary>
        /// <remarks>
        /// Not simply "starts with a slash", which was the first attempt and is wrong on Windows: a
        /// leading slash there means rooted-on-the-current-drive, and the path APIs accept <c>/</c>
        /// as a separator throughout, so <c>/Windows/System32/cmd.exe</c> is a real command that
        /// launches - measured with <c>CreateProcessW</c> on Windows, pid and all. Note the old rule
        /// was also inconsistent with itself: the backslash
        /// spelling of the same drive-relative path was kept while the forward-slash spelling was
        /// substituted, and both launch.
        ///
        /// Requiring a recognisably Unix first segment still catches every realistic case - GetDefaultShell on Unix only ever returns a <c>/bin/</c> path, and
        /// configured shells live under these roots. A path under some other root falls through and
        /// fails visibly, which is the trade made everywhere else here.
        /// </remarks>
        private static bool LooksLikeAUnixCommand(string executable)
        {
            foreach (string root in UnixFilesystemRoots)
            {
                if (executable.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>Roots that name a Unix location and, in practice, not a Windows one.</summary>
        /// <remarks>
        /// "In practice" rather than "never", because a leading slash on Windows is rooted on the
        /// current drive, so each of these is also a spellable Windows path. Every entry was checked
        /// against a real Windows filesystem for a collision before being listed, and one collides:
        /// <c>C:\home\</c> existed on the machine this was verified on, created by some dev tool. So
        /// <c>/home/tools/shell.exe</c> would be substituted there even though it is launchable.
        ///
        /// <c>/home/</c> stays anyway, on a probability judgement rather than a principle: it is the
        /// most common Linux home root and nobody writes a Windows profile command that way. That
        /// makes this list a considered trade, not an invariant - which is why the remarks on
        /// <see cref="LooksLikeAUnixCommand"/> no longer claim these signals cannot mean anything
        /// else.
        ///
        /// <c>/run/</c> is here for NixOS, whose configured system shell is
        /// <c>/run/current-system/sw/bin/bash</c> rather than a <c>/nix/store/</c> path.
        ///
        /// Deliberately absent: <c>/Users/</c>. It looks like the obvious companion to
        /// <c>/home/</c> for macOS, and it is the one entry that must not be added -
        /// <c>C:\Users\</c> exists on every Windows machine, so it would recreate exactly the
        /// false-positive class this list was tightened to remove. The macOS home case is not
        /// reachable this way; it falls through and fails visibly instead.
        ///
        /// Case-insensitive at the point of use, since the comparison happens on Windows.
        /// </remarks>
        private static readonly string[] UnixFilesystemRoots =
            { "/bin/", "/sbin/", "/usr/", "/opt/", "/etc/", "/home/", "/var/", "/run/", "/snap/", "/nix/", "/Library/", "/System/" };

        /// <summary>Suffixes that mark an executable as Windows-addressed.</summary>
        /// <remarks>
        /// <c>.bat</c> and <c>.cmd</c> belong here: they run fine on Windows - <c>CreateProcess</c>
        /// hands them to the command processor - and not at all elsewhere. <c>.ps1</c> is the loose
        /// one, since PowerShell Core runs .ps1 files on Linux too; it stays because a .ps1 is not
        /// directly spawnable there either, so the verdict is right even though the reason is
        /// narrower than "only Windows executes this".
        /// </remarks>
        private static readonly string[] WindowsExecutableSuffixes =
            { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".wsf", ".msc" };

        /// <summary>Shells that exist only on Windows, spelled with or without their suffix. Not
        /// <c>pwsh</c>: PowerShell is cross-platform.</summary>
        private static readonly string[] WindowsOnlyShellNames =
            { "cmd", "cmd.exe", "powershell", "powershell.exe", "wsl", "wsl.exe", "conhost", "conhost.exe" };

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
        /// Extension-aware on Windows, where a command is legitimately written without one -
        /// <c>pwsh</c> runs <c>pwsh.exe</c>, because the launcher appends <c>.exe</c> to a name that
        /// carries no extension of its own. Probing only the literal filename reported those as
        /// missing, which is a pre-existing defect in the callers that use this to decide whether a
        /// profile is usable: an extensionless profile command was reset or hidden.
        ///
        /// Only <c>.exe</c> is appended, matching <c>CreateProcessW</c> with <c>lpApplicationName</c>
        /// NULL (native/src/lib.rs:301). Not PATHEXT, which is a shell's list, and not <c>.com</c> -
        /// measured on Windows, an extensionless name backed only by a <c>.com</c> fails with
        /// ERROR_FILE_NOT_FOUND. portable_pty's own search is more permissive than this; the stricter
        /// of the two launchers is the safe one to model.
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

                if (File.Exists(fullPath)) return true;

                if (OperatingSystem.IsWindows() &&
                    !Path.HasExtension(fullPath) &&
                    File.Exists(fullPath + ".exe"))
                {
                    return true;
                }
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
