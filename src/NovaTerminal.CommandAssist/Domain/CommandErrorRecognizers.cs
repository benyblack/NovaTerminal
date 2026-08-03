using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// One failure class, one function. A recogniser reads a <see cref="CommandErrorSignal"/> and
/// returns the fixes it is prepared to stand behind, or nothing.
/// </summary>
/// <param name="Id">
/// Stable identifier, used by tests and by the ordering tie-break. Kebab-case, scoped by tool:
/// <c>git-no-upstream</c>, <c>npm-missing-script</c>.
/// </param>
/// <param name="Summary">What the recogniser is for, in one line. Read by humans only.</param>
/// <param name="Analyze">
/// Pure. Called for every failing command, so it must decide fast and must not touch the
/// filesystem, the network, or anything outside the signal it is handed - Phase 5 runs this same
/// table behind the provider seam, where side effects would be a correctness problem rather than
/// a style one.
/// </param>
public sealed record CommandErrorRecognizer(
    string Id,
    string Summary,
    Func<CommandErrorSignal, IReadOnlyList<CommandFixSuggestion>> Analyze);

/// <summary>
/// The table. Everything <c>HeuristicErrorInsightService</c> knows about failing commands lives
/// here, one entry per failure class.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Confidence is a promise, not a ranking.</strong> <c>CommandAssistModeRouter</c> opens
/// the Fix popup unprompted at 0.8 and shows a one-line bubble below it, so the number decides
/// whether a suggestion interrupts. The five constants below are the only values used, and the
/// two that cross the threshold are reserved for cases where the fix is near-certain:
/// <see cref="NearCertain"/> for a one-edit typo of a name we know, and
/// <see cref="ToolNamedTheFix"/> for the cases where the failing tool <em>printed the exact
/// command to run</em> and we are only lifting it out of the scrollback. Everything else is an
/// explanation, and explanations ride below the threshold into Suggest mode where they cost the
/// user nothing to ignore.
/// </para>
/// <para>
/// <strong>Sample provenance.</strong> Every pattern here was written against a message captured
/// from a real run where that was possible; <c>CommandErrorRecognizerTests</c> records which ones
/// are live-verified on the development box and which are transcribed from documentation or from
/// upstream source, and that table is the honest inventory. Transcribed patterns are deliberately
/// matched loosely (a distinctive substring rather than a whole line) because the exact wording is
/// the part that could not be checked.
/// </para>
/// <para>
/// <strong>Extending it.</strong> Add an entry to <see cref="All"/>, add its samples to the test
/// theory, and say in the test whether the sample is verified. Do not add branches to the service:
/// the point of the table is that Phase 4b's knowledge catalogue and Phase 5's provider seam can
/// both walk it without knowing what is in it.
/// </para>
/// </remarks>
public static partial class CommandErrorRecognizers
{
    /// <summary>A one-edit typo of a name we know, with the shell saying it could not resolve it.</summary>
    public const double NearCertain = 0.95;

    /// <summary>The failing tool printed the exact command to run; we are quoting it back.</summary>
    public const double ToolNamedTheFix = 0.9;

    /// <summary>A good guess with an obvious alternative reading. Below the Fix threshold on purpose.</summary>
    public const double Likely = 0.7;

    /// <summary>One of several plausible causes.</summary>
    public const double Plausible = 0.55;

    /// <summary>An explanation of what went wrong, with a command attached because the surface needs one.</summary>
    public const double Explanatory = 0.4;

    public static IReadOnlyList<CommandErrorRecognizer> All { get; } =
    [
        new("command-not-found", "The shell could not resolve the program name.", CommandNotFound),
        new("permission-denied", "The file exists but could not be executed or read.", PermissionDenied),
        new("path-not-found", "A file or directory argument does not exist.", PathNotFound),
        new("git-unknown-subcommand", "git rejected the subcommand and usually names the right one.", GitUnknownSubcommand),
        new("git-not-a-repository", "git was run outside a working tree.", GitNotARepository),
        new("git-pathspec", "git could not resolve a branch or path argument.", GitPathspec),
        new("git-no-upstream", "The branch has no upstream; git prints the exact push command.", GitNoUpstream),
        new("git-detached-head", "HEAD is detached, so a bare push has no destination.", GitDetachedHead),
        new("git-rejected", "The remote refused the push because it holds work we do not have.", GitRejected),
        new("npm-missing-script", "package.json has no script by that name.", NpmMissingScript),
        new("npm-eresolve", "npm could not solve the peer-dependency graph.", NpmEresolve),
        new("docker-daemon", "The Docker daemon is not reachable.", DockerDaemon),
        new("docker-no-such-container", "The named container or image does not exist.", DockerNoSuchContainer),
        new("dotnet-sdk", "No .NET SDK matches what global.json asks for.", DotnetSdk),
        new("dotnet-build-error", "The build failed with an MSBuild/NETSDK diagnostic.", DotnetBuildError),
    ];

    // ------------------------------------------------------------------ command not found

    /// <summary>
    /// The largest class by a wide margin, and the only one that reaches
    /// <see cref="NearCertain"/> from a typo.
    /// </summary>
    /// <remarks>
    /// Four wordings, one per shell family, all live-verified except fish:
    /// <list type="bullet">
    /// <item><description>pwsh 7: <c>gti: The term 'gti' is not recognized as a name of a cmdlet,
    /// function, script file, or executable program.</c></description></item>
    /// <item><description>Windows PowerShell 5.1: <c>gti : The term 'gti' is not recognized as the
    /// name of a cmdlet, function, script file, or operable program.</c> - note "the name" rather
    /// than "a name", which is why the pattern does not pin the article.</description></item>
    /// <item><description>cmd: <c>'gti' is not recognized as an internal or external command,</c>
    /// </description></item>
    /// <item><description>bash: <c>/usr/bin/bash: line 1: gti: command not found</c>; zsh puts the
    /// token last (<c>zsh: command not found: gti</c>), which is why the token is extracted with
    /// two patterns.</description></item>
    /// <item><description>fish: <c>fish: Unknown command: gti</c> - transcribed.</description></item>
    /// </list>
    /// </remarks>
    private static IReadOnlyList<CommandFixSuggestion> CommandNotFound(CommandErrorSignal signal)
    {
        if (!IsCommandNotFound(signal))
        {
            return [];
        }

        List<CommandFixSuggestion> results = [];
        string token = ExtractMissingToken(signal) ?? signal.PrimaryToken;
        if (token.Length == 0)
        {
            return [];
        }

        bool tokenIsTheCommand = string.Equals(token, signal.PrimaryToken, StringComparison.OrdinalIgnoreCase);

        string? corrected = FixKnownCommands.TryCorrect(token, out int distance);
        if (corrected != null)
        {
            // The high-confidence case, and the only one a typo gets: a single edit away from a
            // name we know, on the token the shell itself named, with the shell saying in as many
            // words that it could not resolve it. Anything looser is a guess wearing a suit.
            double confidence = distance == 1 && tokenIsTheCommand ? NearCertain : Likely;
            results.Add(new CommandFixSuggestion(
                Title: $"Did you mean {corrected}?",
                SuggestedCommand: tokenIsTheCommand ? signal.WithPrimaryToken(corrected) : corrected,
                Description: $"'{token}' is not a command the shell could resolve; '{corrected}' is one edit away.",
                Confidence: confidence,
                Badges: ["Fix", "Typo"]));
        }

        CommandFixSuggestion? native = TryTranslateToShellNative(signal, token);
        if (native != null)
        {
            results.Add(native);
        }

        CommandFixSuggestion? local = TryRunFromCurrentDirectory(signal, token);
        if (local != null)
        {
            results.Add(local);
        }

        if (results.Count == 0 && tokenIsTheCommand)
        {
            results.Add(new CommandFixSuggestion(
                Title: $"'{token}' is not installed or not on PATH",
                SuggestedCommand: signal.IsWindowsShell ? $"where {token}" : $"command -v {token}",
                Description: "The shell searched PATH and found nothing by that name.",
                Confidence: Explanatory,
                Badges: ["Fix", "PATH"]));
        }

        return results;
    }

    private static bool IsCommandNotFound(CommandErrorSignal signal)
    {
        return signal.OutputContainsAny(
            "command not found",
            "is not recognized as an internal or external command",
            "is not recognized as the name of a cmdlet",
            "is not recognized as a name of a cmdlet",

            // Scoped to fish's own prefix rather than the bare phrase: "unknown command" is what
            // redis-cli, psql and half a dozen REPLs print too, and correcting *their* vocabulary
            // against a list of shell commands is worse than saying nothing.
            "fish: unknown command")

            // PowerShell's message runs to two sentences and wraps; a pane narrow enough, or a
            // capture that clipped the tail, can leave only "The term 'x' is not recognized". The
            // bare phrase alone would be too loose (PowerShell also writes "The argument ... is not
            // recognized"), so it is paired with the token pattern, which only that message
            // produces.
            || (signal.OutputContains("is not recognized") && PowerShellMissingToken().IsMatch(signal.OutputTail));
    }

    /// <summary>
    /// The name the shell says it could not resolve, which is not always the command's first token
    /// (a pipeline's second stage, a subshell, a script's own internal call).
    /// </summary>
    /// <remarks>
    /// zsh is checked before bash on purpose. zsh prints <c>zsh: command not found: gti</c>, and the
    /// bash pattern - "a token, a colon, then the phrase" - matches <c>zsh</c> in that string. Order
    /// is the fix; a negative lookbehind would be the same rule written less legibly.
    /// </remarks>
    private static string? ExtractMissingToken(CommandErrorSignal signal)
    {
        Match match = PowerShellMissingToken().Match(signal.OutputTail);
        if (match.Success)
        {
            return Sanitize(match.Groups[1].Value);
        }

        match = CmdMissingToken().Match(signal.OutputTail);
        if (match.Success)
        {
            return Sanitize(match.Groups[1].Value);
        }

        // zsh: "zsh: command not found: gti"
        match = PosixMissingTokenAfter().Match(signal.OutputTail);
        if (match.Success)
        {
            return Sanitize(match.Groups[1].Value);
        }

        // bash: "/usr/bin/bash: line 1: gti: command not found"
        match = PosixMissingTokenBefore().Match(signal.OutputTail);
        if (match.Success)
        {
            return Sanitize(match.Groups[1].Value);
        }

        match = FishMissingToken().Match(signal.OutputTail);
        return match.Success ? Sanitize(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Rejects the shell's own name and the words its message frame is made of. A pattern that
    /// grabbed <c>bash</c> out of <c>bash: gti: command not found</c> would go on to offer
    /// "did you mean cat?", which is the failure mode this whole file exists to avoid.
    /// </summary>
    private static string? Sanitize(string token)
    {
        string trimmed = token.Trim().Trim('\'', '"', ',', '.');
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed switch
        {
            "bash" or "zsh" or "sh" or "fish" or "pwsh" or "powershell" or "cmd" or "line" => null,
            _ => trimmed,
        };
    }

    // ------------------------------------------------------------------ ./ invocation

    /// <summary>
    /// The hint the design doc calls out by name, and the one that was unreachable for the whole of
    /// V1 because it keyed off an <c>ErrorOutput</c> that was hard-coded to null.
    /// </summary>
    /// <remarks>
    /// Live-verified on Git Bash: running an executable script in the current directory by bare
    /// name gives <c>/usr/bin/bash: line 1: novarun.sh: command not found</c> - a
    /// <em>command-not-found</em>, not a "no such file". POSIX shells do not have <c>.</c> on PATH,
    /// so the file being right there is exactly why the message is confusing. The old
    /// implementation hung this off "No such file or directory", which is the message you get for
    /// an argument rather than for the program; both triggers are kept, because <c>sh missing.sh</c>
    /// does produce the second one.
    /// </remarks>
    private static CommandFixSuggestion? TryRunFromCurrentDirectory(CommandErrorSignal signal, string token)
    {
        if (token.Contains('/') || token.Contains('\\') || token.StartsWith('.'))
        {
            return null; // already an explicit path
        }

        if (!LooksLikeAFile(token))
        {
            return null;
        }

        string prefix = signal.IsWindowsShell ? ".\\" : "./";
        return new CommandFixSuggestion(
            Title: "Run it from the current directory",
            SuggestedCommand: prefix + token,
            Description: signal.IsWindowsShell
                ? "PowerShell will not run a file in the current directory without an explicit path."
                : "POSIX shells do not search the current directory; the file needs an explicit './'.",
            Confidence: Likely,
            Badges: ["Fix", "Path"]);
    }

    private static bool LooksLikeAFile(string token)
    {
        int dot = token.LastIndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        string extension = token[dot..].ToLowerInvariant();
        return extension is ".sh" or ".bash" or ".zsh" or ".fish" or ".ps1" or ".py" or ".rb"
            or ".pl" or ".js" or ".ts" or ".exe" or ".bat" or ".cmd" or ".run" or ".bin" or ".out";
    }

    // ------------------------------------------------------------------ shell-native translation

    /// <summary>
    /// The user typed another shell's name for the thing they wanted. Confidence stays under the
    /// Fix threshold: <c>dir</c> in bash usually means <c>ls</c>, but the user may equally have
    /// meant a program called <c>dir</c> that is not installed, and interrupting them with a popup
    /// to say so is worse than a one-line bubble.
    /// </summary>
    private static CommandFixSuggestion? TryTranslateToShellNative(CommandErrorSignal signal, string token)
    {
        IReadOnlyDictionary<string, string> table = signal.Shell switch
        {
            AssistShellFamily.Posix or AssistShellFamily.Fish => WindowsToPosix,
            AssistShellFamily.Cmd => PosixToCmd,
            AssistShellFamily.PowerShell => PosixToPowerShell,
            _ => EmptyTranslation,
        };

        if (!table.TryGetValue(token, out string? replacement))
        {
            return null;
        }

        return new CommandFixSuggestion(
            Title: $"Use {replacement} in this shell",
            SuggestedCommand: signal.WithPrimaryToken(replacement),
            Description: $"'{token}' belongs to a different shell; '{replacement}' is this one's equivalent.",
            Confidence: Likely,
            Badges: ["Fix", "Shell"]);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTranslation =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> WindowsToPosix =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dir"] = "ls",
            ["cls"] = "clear",
            ["copy"] = "cp",
            ["move"] = "mv",
            ["del"] = "rm",
            ["erase"] = "rm",
            ["findstr"] = "grep",
            ["where"] = "which",
            ["tasklist"] = "ps",
            ["taskkill"] = "kill",
            ["type"] = "cat",
            ["Get-ChildItem"] = "ls",
            ["Set-Location"] = "cd",
            ["Get-Content"] = "cat",
            ["Get-Process"] = "ps",
            ["Remove-Item"] = "rm",
            ["Copy-Item"] = "cp",
            ["Move-Item"] = "mv",
            ["Select-String"] = "grep",
            ["Write-Output"] = "echo",
            ["Write-Host"] = "echo",
        };

    private static readonly IReadOnlyDictionary<string, string> PosixToCmd =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ls"] = "dir",
            ["cat"] = "type",
            ["rm"] = "del",
            ["cp"] = "copy",
            ["mv"] = "move",
            ["clear"] = "cls",
            ["grep"] = "findstr",
            ["which"] = "where",
            ["ps"] = "tasklist",
            ["kill"] = "taskkill",
            ["pwd"] = "cd",
            ["man"] = "help",
        };

    private static readonly IReadOnlyDictionary<string, string> PosixToPowerShell =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Most of the POSIX core is aliased in PowerShell already (ls, cat, cp, mv, rm, pwd,
            // echo, man, cd) and would never reach a not-found. Only the gaps are listed.
            ["grep"] = "Select-String",
            ["which"] = "Get-Command",
            ["touch"] = "New-Item",
            ["df"] = "Get-PSDrive",
            ["uname"] = "Get-ComputerInfo",
            ["find"] = "Get-ChildItem",
        };

    // ------------------------------------------------------------------ permission

    /// <summary>
    /// Transcribed, not live-verified: Git Bash on Windows emulates POSIX permissions over NTFS
    /// ACLs and <c>chmod 000</c> does not actually make a file unreadable there, so this box cannot
    /// produce the message. The two wordings are the GNU coreutils / bash ones
    /// (<c>bash: ./deploy.sh: Permission denied</c>, <c>cat: /etc/shadow: Permission denied</c>)
    /// and the pattern matches the phrase rather than a whole line for exactly that reason.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> PermissionDenied(CommandErrorSignal signal)
    {
        if (!signal.OutputContainsAny("permission denied", "access is denied", "operation not permitted"))
        {
            return [];
        }

        List<CommandFixSuggestion> results = [];
        string? target = ExtractPermissionTarget(signal);

        // The overwhelmingly common shape: a script the user just wrote, never chmod'd, and ran
        // with ./. Offering chmod for `cat /etc/shadow` would be wrong, so it is gated on the
        // command looking like an invocation of the file rather than a read of it.
        if (!signal.IsWindowsShell &&
            target != null &&
            signal.PrimaryToken.Contains(target, StringComparison.Ordinal))
        {
            results.Add(new CommandFixSuggestion(
                Title: "Make it executable",
                SuggestedCommand: $"chmod +x {target}",
                Description: "The file exists but has no execute bit for you.",
                Confidence: Likely,
                Badges: ["Fix", "Permission"]));
        }

        if (!signal.IsWindowsShell)
        {
            results.Add(new CommandFixSuggestion(
                Title: "Run it with elevated privileges",
                SuggestedCommand: "sudo " + signal.CommandText,
                Description: "The operation needs privileges the current user does not have.",
                Confidence: Plausible,
                Badges: ["Fix", "Permission"]));
        }
        else
        {
            results.Add(new CommandFixSuggestion(
                Title: "Access was denied",
                SuggestedCommand: signal.CommandText,
                Description: "Retry from an elevated prompt, or check that nothing else holds the file open.",
                Confidence: Explanatory,
                Badges: ["Fix", "Permission"]));
        }

        return results;
    }

    private static string? ExtractPermissionTarget(CommandErrorSignal signal)
    {
        Match match = PermissionTarget().Match(signal.OutputTail);
        if (!match.Success)
        {
            return null;
        }

        string target = match.Groups[1].Value.Trim();
        return target.Length == 0 ? null : target;
    }

    // ------------------------------------------------------------------ path

    /// <summary>
    /// Live-verified across all four families:
    /// <c>cat: /tmp/definitely-not-here-xyz: No such file or directory</c> (bash),
    /// <c>Get-Content: Cannot find path 'C:\nope\missing.txt' because it does not exist.</c> (pwsh 7),
    /// <c>The system cannot find the path specified.</c> (cmd).
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> PathNotFound(CommandErrorSignal signal)
    {
        bool posixStyle = signal.OutputContains("no such file or directory");
        bool powerShellStyle = signal.OutputContains("because it does not exist");
        bool cmdStyle = signal.OutputContainsAny(
            "the system cannot find the path specified",
            "the system cannot find the file specified");

        if (!posixStyle && !powerShellStyle && !cmdStyle)
        {
            return [];
        }

        List<CommandFixSuggestion> results = [];

        // Kept from V1 and kept deliberately: `sh build.sh` on a missing file reports the argument,
        // not the program, so the ./ hint still belongs here as well as under command-not-found.
        CommandFixSuggestion? local = TryRunFromCurrentDirectory(signal, signal.PrimaryToken);
        if (local != null)
        {
            results.Add(local);
        }

        string? missing = ExtractMissingPath(signal);
        results.Add(new CommandFixSuggestion(
            Title: missing is null ? "A path in this command does not exist" : $"'{missing}' does not exist",
            SuggestedCommand: signal.IsWindowsShell
                ? "Get-ChildItem"
                : "ls -la",
            Description: "List the current directory to check the name and the working directory.",
            Confidence: Explanatory,
            Badges: ["Fix", "Path"]));

        return results;
    }

    private static string? ExtractMissingPath(CommandErrorSignal signal)
    {
        Match match = PowerShellMissingPath().Match(signal.OutputTail);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = PosixMissingPath().Match(signal.OutputTail);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ------------------------------------------------------------------ git

    /// <summary>
    /// git prints the correction itself, which is the whole reason this reaches
    /// <see cref="ToolNamedTheFix"/>. Live-verified: <c>git stauts</c> gives
    /// <c>git: 'stauts' is not a git command. See 'git --help'.</c> followed by a blank line,
    /// <c>The most similar command is</c>, and the name indented on its own line.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> GitUnknownSubcommand(CommandErrorSignal signal)
    {
        Match match = GitUnknownCommand().Match(signal.OutputTail);
        if (!match.Success)
        {
            return [];
        }

        string bad = match.Groups[1].Value;
        string? suggested = ExtractGitMostSimilar(signal.OutputTail);
        if (suggested == null)
        {
            return
            [
                new CommandFixSuggestion(
                    Title: $"git has no '{bad}' subcommand",
                    SuggestedCommand: "git --help",
                    Description: "Check the subcommand name against the list git prints.",
                    Confidence: Explanatory,
                    Badges: ["Fix", "git"]),
            ];
        }

        return
        [
            new CommandFixSuggestion(
                Title: $"Did you mean git {suggested}?",
                SuggestedCommand: string.Equals(signal.SecondToken, bad, StringComparison.Ordinal)
                    ? signal.WithSecondToken(suggested)
                    : $"git {suggested}",
                Description: $"git itself suggests '{suggested}' as the closest subcommand to '{bad}'.",
                Confidence: ToolNamedTheFix,
                Badges: ["Fix", "git"]),
        ];
    }

    /// <summary>
    /// The line after "The most similar command is" / "The most similar commands are", indented.
    /// Done by hand rather than with a multiline regex: the tail's line breaks come from the grid,
    /// so the indentation is whatever width the terminal expanded a tab to, and a pattern pinning
    /// that would be pinning the pane's tab stops.
    /// </summary>
    private static string? ExtractGitMostSimilar(string outputTail)
    {
        string[] lines = outputTail.Split('\n');
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].IndexOf("most similar command", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            for (int j = i + 1; j < lines.Length; j++)
            {
                string candidate = lines[j].Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                // Only ever one token: git lists candidates one per line.
                string first = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (IsPlausibleSubcommand(first))
                {
                    return first;
                }

                break;
            }
        }

        return null;
    }

    private static bool IsPlausibleSubcommand(string token)
        => token.Length is > 0 and <= 32 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Live-verified: <c>fatal: not a git repository (or any of the parent directories): .git</c>.
    /// Explanatory only. <c>git init</c> is attached because the surface needs a runnable command
    /// on every row, and it is the one thing that makes the message go away - but at
    /// <see cref="Explanatory"/>, because initialising a repository in a directory the user merely
    /// wandered into is not a fix, it is a mess.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> GitNotARepository(CommandErrorSignal signal)
    {
        if (!signal.OutputContains("not a git repository"))
        {
            return [];
        }

        return
        [
            new CommandFixSuggestion(
                Title: "This directory is not inside a Git repository",
                SuggestedCommand: "git init",
                Description: "git walked up to the filesystem root without finding a .git directory. "
                    + "Change to the repository first, or initialise one here.",
                Confidence: Explanatory,
                Badges: ["Fix", "git"]),
        ];
    }

    /// <summary>
    /// Live-verified: <c>error: pathspec 'no-such-branch-xyz' did not match any file(s) known to git</c>.
    /// The ambiguity is real - the argument may have been meant as a branch that does not exist yet,
    /// or as a path that is misspelt - so both readings are offered and neither is confident.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> GitPathspec(CommandErrorSignal signal)
    {
        Match match = GitPathspecFailure().Match(signal.OutputTail);
        if (!match.Success)
        {
            return [];
        }

        string pathspec = match.Groups[1].Value;
        List<CommandFixSuggestion> results =
        [
            new CommandFixSuggestion(
                Title: $"Create branch '{pathspec}'?",
                SuggestedCommand: $"git switch -c {pathspec}",
                Description: $"git found no branch, tag or path called '{pathspec}'.",
                Confidence: Plausible,
                Badges: ["Fix", "git"]),
        ];

        if (!pathspec.Contains('/') && !pathspec.Contains('.'))
        {
            results.Add(new CommandFixSuggestion(
                Title: "Fetch first, then try again",
                SuggestedCommand: "git fetch --all",
                Description: "The branch may exist on the remote but not locally yet.",
                Confidence: Explanatory,
                Badges: ["Fix", "git"]));
        }

        return results;
    }

    /// <summary>
    /// Live-verified, and the cleanest case in the table: git prints the literal command.
    /// <code>
    /// fatal: The current branch master has no upstream branch.
    /// To push the current branch and set the remote as upstream, use
    ///
    ///     git push --set-upstream origin master
    /// </code>
    /// The suggestion is that line, lifted verbatim, which is why it is allowed over the Fix
    /// threshold: there is no inference in it at all.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> GitNoUpstream(CommandErrorSignal signal)
    {
        if (!signal.OutputContains("has no upstream branch"))
        {
            return [];
        }

        Match match = GitSetUpstreamCommand().Match(signal.OutputTail);
        if (!match.Success)
        {
            return
            [
                new CommandFixSuggestion(
                    Title: "This branch has no upstream",
                    SuggestedCommand: "git push --set-upstream origin HEAD",
                    Description: "git will not guess where to push a branch it has never pushed.",
                    Confidence: Likely,
                    Badges: ["Fix", "git"]),
            ];
        }

        string command = match.Value.Trim();
        return
        [
            new CommandFixSuggestion(
                Title: "Set the upstream and push",
                SuggestedCommand: command,
                Description: "git printed this exact command in its own error message.",
                Confidence: ToolNamedTheFix,
                Badges: ["Fix", "git"]),
        ];
    }

    /// <summary>
    /// Transcribed: <c>fatal: You are not currently on a branch.</c> plus the
    /// <c>git push origin HEAD:&lt;name-of-remote-branch&gt;</c> line git prints under it. Not
    /// reproducible on this box without leaving the checkout detached, which is not a state a test
    /// harness should create in the developer's own worktree.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> GitDetachedHead(CommandErrorSignal signal)
    {
        if (!signal.OutputContainsAny(
                "you are not currently on a branch",
                "head detached at",
                "you are in 'detached head' state"))
        {
            return [];
        }

        return
        [
            new CommandFixSuggestion(
                Title: "HEAD is detached - push to an explicit branch",
                SuggestedCommand: "git push origin HEAD:main",
                Description: "A detached HEAD has no branch name, so git cannot work out where to push. "
                    + "Replace 'main' with the branch you meant.",
                Confidence: Plausible,
                Badges: ["Fix", "git"]),
        ];
    }

    /// <summary>
    /// Transcribed: <c>! [rejected]</c> / <c>Updates were rejected because the remote contains work
    /// that you do not have locally.</c> Reproducing it needs a writable remote, which a test box
    /// should not be pushing to.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> GitRejected(CommandErrorSignal signal)
    {
        if (!signal.OutputContainsAny(
                "updates were rejected because",
                "! [rejected]",
                "failed to push some refs"))
        {
            return [];
        }

        bool nonFastForward = signal.OutputContainsAny("non-fast-forward", "fetch first", "behind its remote");
        return
        [
            new CommandFixSuggestion(
                Title: "Integrate the remote's commits first",
                SuggestedCommand: "git pull --rebase",
                Description: nonFastForward
                    ? "The remote branch has commits this one does not. Rebase onto them, then push again."
                    : "The remote refused the push. Pull first, then push again.",
                Confidence: nonFastForward ? Likely : Plausible,
                Badges: ["Fix", "git"]),
        ];
    }

    // ------------------------------------------------------------------ npm / pnpm

    /// <summary>
    /// Live-verified on npm 10: <c>npm error Missing script: "definitely-not-a-script"</c> followed
    /// by <c>npm error To see a list of scripts, run:</c> and <c>npm error   npm run</c>. npm 8 and
    /// earlier prefix with <c>npm ERR!</c> instead, which the pattern also accepts; pnpm's wording
    /// (<c>ERR_PNPM_NO_SCRIPT  Missing script: build</c>) is transcribed.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> NpmMissingScript(CommandErrorSignal signal)
    {
        if (!signal.OutputContainsAny("missing script", "err_pnpm_no_script"))
        {
            return [];
        }

        string runner = signal.PrimaryToken.ToLowerInvariant() switch
        {
            "pnpm" => "pnpm",
            "yarn" => "yarn",
            "bun" => "bun",
            _ => "npm",
        };

        Match match = MissingScriptName().Match(signal.OutputTail);
        string? scriptName = match.Success ? match.Groups[1].Value : null;

        return
        [
            new CommandFixSuggestion(
                Title: scriptName is null
                    ? "package.json has no such script"
                    : $"package.json has no '{scriptName}' script",
                SuggestedCommand: runner == "yarn" ? "yarn run" : $"{runner} run",
                Description: "Run the package manager with no script name to list what is defined.",
                Confidence: Likely,
                Badges: ["Fix", "npm"]),
        ];
    }

    /// <summary>
    /// Transcribed from npm's own output format: the <c>ERESOLVE unable to resolve dependency
    /// tree</c> block. Reproducing it needs a package tree with a genuine peer conflict, which is
    /// not something to construct in a test fixture.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> NpmEresolve(CommandErrorSignal signal)
    {
        if (!signal.OutputContains("eresolve"))
        {
            return [];
        }

        return
        [
            new CommandFixSuggestion(
                Title: "Peer dependencies conflict",
                SuggestedCommand: "npm install --legacy-peer-deps",
                Description: "npm could not satisfy every peer range at once. "
                    + "--legacy-peer-deps installs anyway; the conflict is still real.",
                Confidence: Plausible,
                Badges: ["Fix", "npm"]),
        ];
    }

    // ------------------------------------------------------------------ docker

    /// <summary>
    /// The Windows-with-an-unreachable-endpoint form is live-verified
    /// (<c>error during connect: Get "http://.../containers/json": dial tcp ...: connectex: No
    /// connection could be made because the target machine actively refused it.</c>); the two
    /// canonical wordings - <c>Cannot connect to the Docker daemon at
    /// unix:///var/run/docker.sock. Is the docker daemon running?</c> and the named-pipe variant -
    /// are transcribed, since the daemon on this box is up.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> DockerDaemon(CommandErrorSignal signal)
    {
        if (!signal.OutputContainsAny(
                "cannot connect to the docker daemon",
                "is the docker daemon running",
                "error during connect",
                "open //./pipe/docker_engine"))
        {
            return [];
        }

        return
        [
            new CommandFixSuggestion(
                Title: "The Docker daemon is not reachable",
                SuggestedCommand: signal.IsWindowsShell ? "docker version" : "systemctl status docker",
                Description: "The CLI is installed but nothing answered on the Docker socket. "
                    + "Start Docker Desktop or the daemon and try again.",
                Confidence: Likely,
                Badges: ["Fix", "docker"]),
        ];
    }

    /// <summary>
    /// Live-verified: <c>Error response from daemon: No such container: no-such-container-xyz</c>.
    /// The image and volume forms differ only in the noun.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> DockerNoSuchContainer(CommandErrorSignal signal)
    {
        Match match = DockerNoSuchObject().Match(signal.OutputTail);
        if (!match.Success)
        {
            return [];
        }

        string noun = match.Groups[1].Value.ToLowerInvariant();
        string name = match.Groups[2].Value;
        string listCommand = noun switch
        {
            "image" => "docker images",
            "volume" => "docker volume ls",
            "network" => "docker network ls",
            _ => "docker ps -a",
        };

        return
        [
            new CommandFixSuggestion(
                Title: $"No {noun} called '{name}'",
                SuggestedCommand: listCommand,
                Description: noun == "container"
                    ? "A stopped container still needs 'docker ps -a' to show up."
                    : $"List the {noun}s to check the name.",
                Confidence: Likely,
                Badges: ["Fix", "docker"]),
        ];
    }

    // ------------------------------------------------------------------ dotnet

    /// <summary>
    /// Live-verified by pointing <c>global.json</c> at an SDK version that is not installed:
    /// <c>A compatible .NET SDK was not found.</c> / <c>Requested SDK version: 1.2.300</c> /
    /// <c>global.json file: ...</c>.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> DotnetSdk(CommandErrorSignal signal)
    {
        if (!signal.OutputContainsAny(
                "a compatible .net sdk was not found",
                "was not found. check that it is installed"))
        {
            return [];
        }

        Match match = RequestedSdkVersion().Match(signal.OutputTail);
        string? requested = match.Success ? match.Groups[1].Value : null;

        return
        [
            new CommandFixSuggestion(
                Title: requested is null
                    ? "No matching .NET SDK is installed"
                    : $"SDK {requested} (from global.json) is not installed",
                SuggestedCommand: "dotnet --list-sdks",
                Description: "Either install the requested SDK or edit global.json to name one that is installed.",
                Confidence: Likely,
                Badges: ["Fix", "dotnet"]),
        ];
    }

    /// <summary>
    /// Live-verified for MSB1009 (<c>MSBUILD : error MSB1009: Project file does not exist.</c>);
    /// the NETSDK wording is the standard MSBuild diagnostic shape, matched generically rather than
    /// per code because there are several hundred of them and none of them have a mechanical fix.
    /// </summary>
    private static IReadOnlyList<CommandFixSuggestion> DotnetBuildError(CommandErrorSignal signal)
    {
        Match match = MsBuildDiagnostic().Match(signal.OutputTail);
        if (!match.Success)
        {
            return [];
        }

        string code = match.Groups[1].Value.ToUpperInvariant();
        string? summary = ExtractDiagnosticText(signal.OutputTail, code);

        return
        [
            new CommandFixSuggestion(
                Title: summary is null ? $"Build failed: {code}" : $"{code}: {summary}",
                SuggestedCommand: code == "MSB1009"
                    ? "dotnet build"
                    : "dotnet build -v normal",
                Description: code == "MSB1009"
                    ? "MSBuild was pointed at a project file that does not exist."
                    : "Re-run with normal verbosity to see which target produced the diagnostic.",
                Confidence: Explanatory,
                Badges: ["Fix", "dotnet"]),
        ];
    }

    private static string? ExtractDiagnosticText(string outputTail, string code)
    {
        int index = outputTail.IndexOf(code, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        int colon = outputTail.IndexOf(':', index);
        if (colon < 0)
        {
            return null;
        }

        int end = outputTail.IndexOf('\n', colon);
        string text = (end < 0 ? outputTail[(colon + 1)..] : outputTail[(colon + 1)..end]).Trim();
        return text.Length is 0 or > 120 ? null : text;
    }

    // ------------------------------------------------------------------ patterns

    [GeneratedRegex(@"The term '([^']+)' is not recognized", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellMissingToken();

    [GeneratedRegex(@"'([^']+)' is not recognized as an internal or external command", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CmdMissingToken();

    [GeneratedRegex(@"([^\s:]+): command not found", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosixMissingTokenBefore();

    [GeneratedRegex(@"command not found:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosixMissingTokenAfter();

    [GeneratedRegex(@"fish: Unknown command:?\s*'?([^'\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FishMissingToken();

    [GeneratedRegex(@"([^\s:]+): Permission denied", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PermissionTarget();

    [GeneratedRegex(@"Cannot find path '([^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellMissingPath();

    [GeneratedRegex(@"([^\s:]+): No such file or directory", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosixMissingPath();

    [GeneratedRegex(@"git: '([^']+)' is not a git command", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitUnknownCommand();

    [GeneratedRegex(@"error: pathspec '([^']+)' did not match", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitPathspecFailure();

    [GeneratedRegex(@"git push --set-upstream \S+ \S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitSetUpstreamCommand();

    [GeneratedRegex(@"Missing script:\s*""?([^""\n]+?)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex MissingScriptName();

    [GeneratedRegex(@"No such (container|image|volume|network):\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DockerNoSuchObject();

    [GeneratedRegex(@"Requested SDK version:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestedSdkVersion();

    [GeneratedRegex(@"\berror (MSB\d{4}|NETSDK\d{4}|CS\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildDiagnostic();
}
