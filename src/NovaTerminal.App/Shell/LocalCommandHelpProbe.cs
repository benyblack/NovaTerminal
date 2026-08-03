using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NovaTerminal.CommandAssist.Domain;

namespace NovaTerminal.Shell;

/// <summary>
/// The App-side implementation of <see cref="ICommandHelpProbe"/>: works out how the user would open
/// full help for a command on <em>this</em> machine, without running anything (V2 Phase 4b, Phase 4
/// task 3, source (b)).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it lives in the App.</strong> It reads <c>PATH</c>, <c>PATHEXT</c> and the filesystem,
/// and it asks what platform it is on - host facts, which the assist assembly's contract keeps out of
/// itself. The seam is <see cref="ICommandHelpProbe"/>; this is the one production implementation and
/// the reason it is a seam at all is that a test can answer the same question deterministically.
/// </para>
/// <para>
/// <strong>What "plausibly exists" means, per shell.</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>pwsh</c> / <c>powershell</c>: <c>Get-Help &lt;token&gt;</c>, offered unconditionally. Every
///     PowerShell session has <c>Get-Help</c>, and it answers for cmdlets, functions, aliases and
///     external executables alike - there is nothing to check, and checking <c>PATH</c> for a cmdlet
///     would answer the wrong question, since a cmdlet is not a file.
///     </description>
///   </item>
///   <item>
///     <description>
///     POSIX shells: <c>man &lt;token&gt;</c> when a man page for the token is on <c>MANPATH</c> (or
///     the conventional roots), else <c>&lt;token&gt; --help</c> when the executable is on
///     <c>PATH</c>. Man first because it is the fuller document; <c>--help</c> is the fallback for the
///     large population of modern tools that ship no man page.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>cmd</c>: <c>&lt;token&gt; /?</c> when the executable is on <c>PATH</c>. Not <c>--help</c>,
///     which most Windows console tools do not understand, and not <c>help &lt;token&gt;</c>, which
///     only knows the shell's own built-ins.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Two-token commands.</strong> Only the first token is looked for on <c>PATH</c> - there is
/// no <c>git-rebase</c> executable - but the offered command carries the whole thing, because
/// <c>git rebase --help</c> and <c>man git-rebase</c> are both real and both better answers than the
/// help for <c>git</c>.
/// </para>
/// <para>
/// <strong>Existence checks only, and cached.</strong> Nothing here starts a process; see the seam's
/// remarks for why. The <c>PATH</c> scan is a handful of <see cref="File.Exists"/> calls and the
/// result is cached per (token, shell) for the life of the process, so the second Help on the same
/// command costs a dictionary lookup. The cache is never invalidated: a tool appearing on
/// <c>PATH</c> mid-session is rare enough, and the cost of being one restart stale is one row that
/// says <c>--help</c> instead of <c>man</c>.
/// </para>
/// </remarks>
public sealed class LocalCommandHelpProbe : ICommandHelpProbe
{
    private readonly ConcurrentDictionary<string, CommandHelpProbeResult?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> _executableExists;
    private readonly Func<string, bool> _manPageExists;

    public LocalCommandHelpProbe()
        : this(ExecutableExistsOnPath, ManPageExists)
    {
    }

    /// <summary>Test seam: both filesystem questions, answered by the caller.</summary>
    internal LocalCommandHelpProbe(Func<string, bool> executableExists, Func<string, bool> manPageExists)
    {
        _executableExists = executableExists;
        _manPageExists = manPageExists;
    }

    public CommandHelpProbeResult? Probe(string commandToken, string? shellKind)
    {
        if (string.IsNullOrWhiteSpace(commandToken))
        {
            return null;
        }

        string token = commandToken.Trim();
        return _cache.GetOrAdd($"{shellKind ?? "?"}{token}", _ => ProbeCore(token, shellKind));
    }

    private CommandHelpProbeResult? ProbeCore(string token, string? shellKind)
    {
        string executable = token.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;

        if (IsPowerShell(shellKind))
        {
            return new CommandHelpProbeResult(
                $"Get-Help {token}",
                "Open PowerShell's own help for this command.");
        }

        if (IsCmd(shellKind))
        {
            return _executableExists(executable)
                ? new CommandHelpProbeResult($"{token} /?", "Print the command's built-in usage text.")
                : null;
        }

        // Unknown shell is treated as POSIX rather than skipped. The rows below are the portable
        // answer, and offering `man tar` in a session whose shell we failed to identify is a far
        // smaller cost than offering nothing in every SSH session, where the shell kind is exactly
        // what is hardest to know.
        string manPageName = token.Replace(' ', '-');
        if (_manPageExists(manPageName))
        {
            return new CommandHelpProbeResult($"man {manPageName}", "Open the manual page for this command.");
        }

        return _executableExists(executable)
            ? new CommandHelpProbeResult($"{token} --help", "Print the command's own help output.")
            : null;
    }

    private static bool IsPowerShell(string? shellKind)
    {
        return shellKind != null &&
               (shellKind.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                shellKind.Equals("powershell", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCmd(string? shellKind)
    {
        return shellKind != null &&
               (shellKind.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                shellKind.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ExecutableExistsOnPath(string executable)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            IReadOnlyList<string> extensions = BuildExecutableExtensions();

            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = directory.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                foreach (string extension in extensions)
                {
                    if (File.Exists(Path.Combine(trimmed, executable + extension)))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // A malformed PATH entry, a drive that is not there, a permission fault: all of them
            // mean "could not tell", and the row is optional, so they mean "no row".
        }

        return false;
    }

    private static IReadOnlyList<string> BuildExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [string.Empty];
        }

        string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var extensions = new List<string> { string.Empty };
        extensions.AddRange(pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        return extensions;
    }

    private static bool ManPageExists(string manPageName)
    {
        if (OperatingSystem.IsWindows())
        {
            // `man` on Windows means a Git-for-Windows or WSL shell, and neither one's man tree is
            // where this process can see it. Answering "no" sends the POSIX branch to `--help`,
            // which is the row that will actually work if the user is in one of those.
            return false;
        }

        try
        {
            IEnumerable<string> roots = BuildManRoots();
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                // Sections 1 and 8 only, and no recursion below the section directory: those are
                // where a command's page lives, and walking the whole tree to find a page nobody
                // asked for yet is the kind of cost this probe exists to avoid.
                foreach (string section in new[] { "man1", "man8" })
                {
                    string sectionPath = Path.Combine(root, section);
                    if (!Directory.Exists(sectionPath))
                    {
                        continue;
                    }

                    if (Directory.EnumerateFiles(sectionPath, manPageName + ".*").Any())
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<string> BuildManRoots()
    {
        string? manPath = Environment.GetEnvironmentVariable("MANPATH");
        if (!string.IsNullOrEmpty(manPath))
        {
            foreach (string entry in manPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return entry;
            }
        }

        // The conventional roots, for the common case of an unset MANPATH (man derives it from
        // /etc/manpath.config, which this process is not going to parse).
        yield return "/usr/share/man";
        yield return "/usr/local/share/man";
        yield return "/opt/homebrew/share/man";
    }
}
