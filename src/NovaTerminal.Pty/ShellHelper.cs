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
        /// Deliberately conservative: it fires only on signals that cannot mean anything else, so a
        /// command it does not recognise is left alone rather than replaced. Missing the occasional
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
        /// launches - measured with <c>CreateProcessW</c> on Windows, pid and all. Flagging it broke
        /// the rule this predicate is supposed to keep, that it fires only on signals which cannot
        /// mean anything else. Note the old rule was also inconsistent with itself: the backslash
        /// spelling of the same drive-relative path was kept while the forward-slash spelling was
        /// substituted, and both launch.
        ///
        /// Requiring a recognisably Unix first segment keeps the rule honest and still catches every
        /// realistic case - GetDefaultShell on Unix only ever returns a <c>/bin/</c> path, and
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

        /// <summary>Roots that exist on Unix and not on Windows. Case-insensitive at the point of
        /// use, since the comparison happens on a Windows machine.</summary>
        private static readonly string[] UnixFilesystemRoots =
            { "/bin/", "/sbin/", "/usr/", "/opt/", "/etc/", "/home/", "/var/", "/snap/", "/nix/", "/Library/", "/System/" };

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
