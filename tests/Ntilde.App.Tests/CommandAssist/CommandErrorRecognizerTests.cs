using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The Fix-mode recogniser table, exercised against the messages the tools actually print.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sample provenance.</strong> Every string in this file is either <em>live</em> - captured
/// by running the failing command on the development box while writing V2 Phase 4a - or
/// <em>transcribed</em>, meaning it comes from the tool's documentation or source because the
/// failure could not be produced here. Each test says which, and
/// <see cref="EveryRecognizerHasASampleAndAProvenance"/> keeps the inventory honest by failing when
/// a recogniser is added to the table without one.
/// </para>
/// <para>
/// Why it matters: a transcribed pattern is a guess about wording. They are therefore matched
/// loosely - a distinctive substring rather than a whole line - and none of them is allowed over
/// the Fix threshold, because a popup that opens itself on a pattern nobody has seen fire is worse
/// than a bubble that does not.
/// </para>
/// <para>
/// Live samples that could not be produced on this box, and why:
/// <list type="bullet">
/// <item><description><em>Permission denied</em> - Git Bash emulates POSIX permissions over NTFS
/// ACLs, and <c>chmod 000</c> followed by <c>cat</c> reads the file back happily.</description></item>
/// <item><description><em>Docker daemon down</em> - the daemon on this box is up; the "error during
/// connect" prefix was captured by pointing <c>DOCKER_HOST</c> at a dead port, but the two
/// canonical socket wordings were not.</description></item>
/// <item><description><em>npm ERESOLVE</em> - needs a package tree with a genuine peer conflict.
/// </description></item>
/// <item><description><em>git detached-head push</em> and <em>git rejected push</em> - one needs
/// the developer's own checkout detached, the other a writable remote.</description></item>
/// <item><description><em>zsh and fish</em> - neither shell is installed on this box. Both
/// wordings are from upstream source.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class CommandErrorRecognizerTests
{
    // ================================================================= command not found

    /// <summary>LIVE: pwsh 7.6.3, <c>pwsh -NoProfile -Command "gti status"</c>.</summary>
    private const string PwshNotRecognized =
        "gti: The term 'gti' is not recognized as a name of a cmdlet, function, script file, or executable program.\n"
        + "Check the spelling of the name, or if a path was included, verify that the path is correct and try again.";

    /// <summary>LIVE: Windows PowerShell 5.1. Note "the name" where pwsh 7 says "a name".</summary>
    private const string WindowsPowerShellNotRecognized =
        "gti : The term 'gti' is not recognized as the name of a cmdlet, function, script file, or operable program. "
        + "Check the spelling of the name, or if a path was included, verify that the path is correct and try again.\n"
        + "At line:1 char:1\n"
        + "+ gti status\n"
        + "+ ~~~\n"
        + "    + CategoryInfo          : ObjectNotFound: (gti:String) [], CommandNotFoundException\n"
        + "    + FullyQualifiedErrorId : CommandNotFoundException";

    /// <summary>LIVE: cmd.exe.</summary>
    private const string CmdNotRecognized =
        "'gti' is not recognized as an internal or external command,\n"
        + "operable program or batch file.";

    /// <summary>LIVE: bash 5.x under Git for Windows.</summary>
    private const string BashCommandNotFound =
        "/usr/bin/bash: line 1: gti: command not found";

    /// <summary>TRANSCRIBED: zsh puts the token last. zsh is not installed on this box.</summary>
    private const string ZshCommandNotFound = "zsh: command not found: gti";

    /// <summary>TRANSCRIBED: fish 3.x. fish is not installed on this box.</summary>
    private const string FishUnknownCommand = "fish: Unknown command: gti";

    [Theory]
    [InlineData("pwsh", PwshNotRecognized)]
    [InlineData("powershell", WindowsPowerShellNotRecognized)]
    [InlineData("cmd", CmdNotRecognized)]
    [InlineData("bash", BashCommandNotFound)]
    [InlineData("zsh", ZshCommandNotFound)]
    [InlineData("fish", FishUnknownCommand)]
    public async Task ATypoOfAKnownCommand_IsAHighConfidenceFixOnEveryShell(string shell, string output)
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze("gti status", 127, shell, output);

        CommandFixSuggestion top = Assert.Single(result, item => item.Confidence >= 0.8);
        Assert.Equal("git status", top.SuggestedCommand);
        Assert.Contains("Typo", top.Badges!);
    }

    /// <summary>
    /// Every command-not-found fix publishes the unresolved name, on every shell, because the name is
    /// what the retroactive history sweep is keyed on (dogfood round 4, item 4a).
    /// </summary>
    [Theory]
    [InlineData("pwsh", PwshNotRecognized)]
    [InlineData("powershell", WindowsPowerShellNotRecognized)]
    [InlineData("cmd", CmdNotRecognized)]
    [InlineData("bash", BashCommandNotFound)]
    [InlineData("zsh", ZshCommandNotFound)]
    [InlineData("fish", FishUnknownCommand)]
    public async Task ACommandNotFound_PublishesTheUnresolvedName(string shell, string output)
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze("gti status", 127, shell, output);

        Assert.All(
            result.Where(item => item.IsCommandNotFound),
            item => Assert.Equal("gti", item.UnresolvedCommandToken));
    }

    /// <summary>
    /// And it is withheld when the unresolved name came from inside the command rather than from the
    /// command position. This is the conjunct that keeps the sweep from flagging every <c>npm</c> line
    /// in a user's history because one script it ran was missing a tool.
    /// </summary>
    [Fact]
    public async Task ACommandNotFoundRaisedFromInsideTheCommand_WithholdsTheUnresolvedName()
    {
        IReadOnlyList<CommandFixSuggestion> result =
            await Analyze("npm run build", 127, "bash", "/usr/bin/bash: line 1: rimraf: command not found");

        Assert.All(
            result.Where(item => item.IsCommandNotFound),
            item => Assert.Null(item.UnresolvedCommandToken));
    }

    /// <summary>
    /// The transposition case is why the distance metric has a transposition term at all: under
    /// plain Levenshtein <c>gti</c> is two edits from <c>git</c>, which is outside the budget a
    /// three-character token gets, and the most recognisable typo in the world would produce
    /// nothing.
    /// </summary>
    [Fact]
    public void TransposedCharactersCountAsOneEdit()
    {
        Assert.Equal(1, FixKnownCommands.LevenshteinDistance("gti", "git"));
        Assert.Equal(1, FixKnownCommands.LevenshteinDistance("dokcer", "docker"));
    }

    /// <summary>
    /// LIVE (bash): running an executable script in the current directory by bare name is a
    /// <em>command not found</em>, not a "no such file" - POSIX shells do not search <c>.</c>.
    /// This is the hint the V2 design doc names, and it was unreachable for all of V1.
    /// </summary>
    [Fact]
    public async Task AScriptInTheCurrentDirectory_GetsTheDotSlashHint()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "novarun.sh",
            127,
            "bash",
            "/usr/bin/bash: line 1: novarun.sh: command not found");

        Assert.Contains(result, item => item.SuggestedCommand == "./novarun.sh");
    }

    [Fact]
    public async Task OnWindowsShells_TheHintUsesABackslash()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "deploy.ps1",
            1,
            "pwsh",
            "deploy.ps1: The term 'deploy.ps1' is not recognized as a name of a cmdlet.");

        Assert.Contains(result, item => item.SuggestedCommand == ".\\deploy.ps1");
    }

    [Fact]
    public async Task AWindowsCommandInBash_IsTranslatedToTheShellNative()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "dir /w",
            127,
            "bash",
            "bash: dir: command not found");

        CommandFixSuggestion suggestion = Assert.Single(result, item => item.SuggestedCommand.StartsWith("ls", StringComparison.Ordinal));
        Assert.True(suggestion.Confidence < 0.8, "a shell-dialect guess must not open the popup by itself");
    }

    [Fact]
    public async Task APosixCommandInCmd_IsTranslatedToTheShellNative()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "ls -la",
            1,
            "cmd",
            "'ls' is not recognized as an internal or external command,\noperable program or batch file.");

        Assert.Contains(result, item => item.SuggestedCommand.StartsWith("dir", StringComparison.Ordinal));
    }

    /// <summary>
    /// A name nothing is close to still gets an answer, because "it is not installed" is the useful
    /// thing to say and the alternative is an empty popup.
    /// </summary>
    [Fact]
    public async Task AnUnknownProgram_GetsAPathExplanationRatherThanAGuess()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "kubeseal --version",
            127,
            "bash",
            "bash: kubeseal: command not found");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Contains("PATH", suggestion.Badges!);
        Assert.True(suggestion.Confidence < 0.8);
    }

    /// <summary>
    /// The shell's own name appears in every one of these messages, immediately before a colon. A
    /// pattern that grabbed it would go on to offer "did you mean cat?" for every failure in bash.
    /// </summary>
    [Fact]
    public async Task TheShellsOwnNameIsNeverMistakenForTheMissingCommand()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "kubeseal",
            127,
            "zsh",
            "zsh: command not found: kubeseal");

        Assert.DoesNotContain(result, item => item.SuggestedCommand.Contains("cat", StringComparison.Ordinal));
        Assert.DoesNotContain(result, item => item.Title.Contains("zsh", StringComparison.Ordinal));
    }

    // ================================================================= permission

    /// <summary>
    /// TRANSCRIBED: bash's exec-permission message. Git Bash on Windows will not produce it (see
    /// the class remarks), so the wording is from bash's source.
    /// </summary>
    [Fact]
    public async Task PermissionDeniedOnAScript_SuggestsChmod()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "./deploy.sh",
            126,
            "bash",
            "bash: ./deploy.sh: Permission denied");

        Assert.Contains(result, item => item.SuggestedCommand == "chmod +x ./deploy.sh");
        Assert.Contains(result, item => item.SuggestedCommand == "sudo ./deploy.sh");
        Assert.All(result, item => Assert.True(item.Confidence < 0.8, $"'{item.Title}' must not auto-open"));
    }

    /// <summary>chmod is only offered for the program, never for an argument the command read.</summary>
    [Fact]
    public async Task PermissionDeniedOnAnArgument_DoesNotSuggestChmodOnIt()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "cat /etc/shadow",
            1,
            "bash",
            "cat: /etc/shadow: Permission denied");

        Assert.DoesNotContain(result, item => item.SuggestedCommand.StartsWith("chmod", StringComparison.Ordinal));
        Assert.Contains(result, item => item.SuggestedCommand == "sudo cat /etc/shadow");
    }

    // ================================================================= paths

    /// <summary>LIVE: <c>pwsh -Command "Get-Content C:\nope\missing.txt"</c>.</summary>
    [Fact]
    public async Task PowerShellCannotFindPath_NamesTheMissingPath()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "Get-Content C:\\nope\\missing.txt",
            1,
            "pwsh",
            "Get-Content: Cannot find path 'C:\\nope\\missing.txt' because it does not exist.");

        Assert.Contains(result, item => item.Title.Contains("C:\\nope\\missing.txt", StringComparison.Ordinal));
    }

    /// <summary>LIVE: <c>cmd /c "cd C:\definitely\not\here"</c>.</summary>
    [Fact]
    public async Task CmdCannotFindPath_IsRecognized()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "cd C:\\definitely\\not\\here",
            1,
            "cmd",
            "The system cannot find the path specified.");

        Assert.NotEmpty(result);
        Assert.All(result, item => Assert.Contains("Path", item.Badges!));
    }

    /// <summary>LIVE: <c>cat /tmp/definitely-not-here-xyz</c> under Git Bash.</summary>
    [Fact]
    public async Task PosixNoSuchFile_IsRecognized()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "cat /tmp/definitely-not-here-xyz",
            1,
            "bash",
            "cat: /tmp/definitely-not-here-xyz: No such file or directory");

        Assert.Contains(result, item => item.Title.Contains("/tmp/definitely-not-here-xyz", StringComparison.Ordinal));
    }

    // ================================================================= git

    /// <summary>LIVE: <c>git status</c> in an empty temp directory.</summary>
    [Fact]
    public async Task GitOutsideARepository_ExplainsRatherThanActs()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git status",
            128,
            "pwsh",
            "fatal: not a git repository (or any of the parent directories): .git");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Contains("not inside a Git repository", suggestion.Title, StringComparison.Ordinal);

        // git init in a directory the user merely wandered into is a mess, not a fix.
        Assert.True(suggestion.Confidence < 0.8);
    }

    /// <summary>LIVE: <c>git stauts</c>. git prints the correction itself, hence the confidence.</summary>
    [Fact]
    public async Task GitNamingTheClosestSubcommand_IsLiftedVerbatim()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git stauts",
            1,
            "pwsh",
            "git: 'stauts' is not a git command. See 'git --help'.\n"
            + "\n"
            + "The most similar command is\n"
            + "        status");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal("git status", suggestion.SuggestedCommand);
        Assert.True(suggestion.Confidence >= 0.8, "git named the fix; there is no inference to discount");
    }

    /// <summary>LIVE: the same failure with the "most similar" block absent (git config dependent).</summary>
    [Fact]
    public async Task GitRejectingASubcommandWithNoSuggestion_StaysExplanatory()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git flurb",
            1,
            "bash",
            "git: 'flurb' is not a git command. See 'git --help'.");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.True(suggestion.Confidence < 0.8);
    }

    /// <summary>LIVE: <c>git checkout no-such-branch-xyz</c> inside a repository.</summary>
    [Fact]
    public async Task GitPathspecFailure_OffersBothReadings()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git checkout no-such-branch-xyz",
            1,
            "pwsh",
            "error: pathspec 'no-such-branch-xyz' did not match any file(s) known to git");

        Assert.Contains(result, item => item.SuggestedCommand == "git switch -c no-such-branch-xyz");
        Assert.Contains(result, item => item.SuggestedCommand == "git fetch --all");
        Assert.All(result, item => Assert.True(item.Confidence < 0.8));
    }

    /// <summary>
    /// LIVE: <c>git push</c> on a branch with no upstream. The cleanest case in the table - git
    /// prints the literal command, and the suggestion is that line lifted out of the scrollback.
    /// </summary>
    [Fact]
    public async Task GitNoUpstream_QuotesGitsOwnCommandBack()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git push",
            128,
            "pwsh",
            "fatal: The current branch master has no upstream branch.\n"
            + "To push the current branch and set the remote as upstream, use\n"
            + "\n"
            + "    git push --set-upstream origin master\n"
            + "\n"
            + "To have this happen automatically for branches without a tracking\n"
            + "upstream, see 'push.autoSetupRemote' in 'git help config'.");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal("git push --set-upstream origin master", suggestion.SuggestedCommand);
        Assert.True(suggestion.Confidence >= 0.8);
    }

    /// <summary>TRANSCRIBED: needs a detached checkout, which a test must not create here.</summary>
    [Fact]
    public async Task GitDetachedHeadPush_IsExplained()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git push",
            128,
            "bash",
            "fatal: You are not currently on a branch.\n"
            + "To push the history leading to the current (detached HEAD)\n"
            + "state now, use\n"
            + "\n"
            + "    git push origin HEAD:<name-of-remote-branch>");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.StartsWith("git push origin HEAD:", suggestion.SuggestedCommand, StringComparison.Ordinal);
        Assert.True(suggestion.Confidence < 0.8, "the branch name is a guess");
    }

    /// <summary>TRANSCRIBED: needs a writable remote.</summary>
    [Fact]
    public async Task GitRejectedPush_SuggestsPullingFirst()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git push",
            1,
            "bash",
            " ! [rejected]        main -> main (non-fast-forward)\n"
            + "error: failed to push some refs to 'https://example.com/x.git'\n"
            + "hint: Updates were rejected because the tip of your current branch is behind\n"
            + "hint: its remote counterpart. Integrate the remote changes (e.g.\n"
            + "hint: 'git pull ...') before pushing again.");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal("git pull --rebase", suggestion.SuggestedCommand);
        Assert.True(suggestion.Confidence < 0.8);
    }

    // ================================================================= npm

    /// <summary>LIVE: npm 10, <c>npm run definitely-not-a-script</c>.</summary>
    [Fact]
    public async Task NpmMissingScript_NamesTheScriptAndOffersTheList()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "npm run definitely-not-a-script",
            1,
            "pwsh",
            "npm error Missing script: \"definitely-not-a-script\"\n"
            + "npm error\n"
            + "npm error To see a list of scripts, run:\n"
            + "npm error   npm run\n"
            + "npm error A complete log of this run can be found in: C:\\Users\\x\\_logs\\debug-0.log");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Contains("definitely-not-a-script", suggestion.Title, StringComparison.Ordinal);
        Assert.Equal("npm run", suggestion.SuggestedCommand);
    }

    /// <summary>TRANSCRIBED: pnpm's wording. Same recogniser, different runner in the fix.</summary>
    [Fact]
    public async Task PnpmMissingScript_SuggestsPnpm()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "pnpm run build",
            1,
            "bash",
            "ERR_PNPM_NO_SCRIPT  Missing script: build");

        Assert.Contains(result, item => item.SuggestedCommand == "pnpm run");
    }

    /// <summary>TRANSCRIBED: needs a real peer-dependency conflict to reproduce.</summary>
    [Fact]
    public async Task NpmEresolve_SuggestsLegacyPeerDeps()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "npm install",
            1,
            "bash",
            "npm error code ERESOLVE\n"
            + "npm error ERESOLVE unable to resolve dependency tree\n"
            + "npm error Found: react@19.0.0");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal("npm install --legacy-peer-deps", suggestion.SuggestedCommand);
        Assert.True(suggestion.Confidence < 0.8);
    }

    // ================================================================= docker

    /// <summary>
    /// LIVE (prefix only): <c>DOCKER_HOST=tcp://127.0.0.1:1 docker ps</c>. The two canonical
    /// socket wordings below it are TRANSCRIBED - the daemon on this box is running.
    /// </summary>
    [Theory]
    [InlineData("error during connect: Get \"http://127.0.0.1:1/v1.55/containers/json\": dial tcp 127.0.0.1:1: connectex: No connection could be made because the target machine actively refused it.")]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    [InlineData("error during connect: ... open //./pipe/docker_engine: The system cannot find the file specified.")]
    public async Task DockerDaemonUnreachable_IsRecognized(string output)
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze("docker ps", 1, "pwsh", output);

        Assert.Contains(result, item => item.Title.Contains("Docker daemon is not reachable", StringComparison.Ordinal));
    }

    /// <summary>LIVE: <c>docker logs no-such-container-xyz</c>.</summary>
    [Fact]
    public async Task DockerNoSuchContainer_PointsAtTheStoppedContainerList()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "docker logs no-such-container-xyz",
            1,
            "pwsh",
            "Error response from daemon: No such container: no-such-container-xyz");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal("docker ps -a", suggestion.SuggestedCommand);
        Assert.Contains("no-such-container-xyz", suggestion.Title, StringComparison.Ordinal);
    }

    // ================================================================= dotnet

    /// <summary>LIVE: a <c>global.json</c> pinned to an SDK version that is not installed.</summary>
    [Fact]
    public async Task DotnetSdkNotFound_NamesTheRequestedVersion()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "dotnet build",
            1,
            "pwsh",
            "  * You intended to execute a .NET SDK command:\n"
            + "      A compatible .NET SDK was not found.\n"
            + "\n"
            + "Requested SDK version: 1.2.300\n"
            + "global.json file: C:\\tmp\\global.json");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Contains("1.2.300", suggestion.Title, StringComparison.Ordinal);
        Assert.Equal("dotnet --list-sdks", suggestion.SuggestedCommand);
    }

    /// <summary>LIVE: <c>dotnet build C:\definitely\not\here\x.csproj</c>.</summary>
    [Fact]
    public async Task MsBuildDiagnostic_IsSurfacedWithItsText()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "dotnet build C:\\definitely\\not\\here\\x.csproj",
            1,
            "pwsh",
            "MSBUILD : error MSB1009: Project file does not exist.\n"
            + "Switch: C:\\definitely\\not\\here\\x.csproj");

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Contains("MSB1009", suggestion.Title, StringComparison.Ordinal);
        Assert.Contains("Project file does not exist", suggestion.Title, StringComparison.Ordinal);
    }

    /// <summary>TRANSCRIBED shape: any NETSDK/CS diagnostic surfaces its first line.</summary>
    [Fact]
    public async Task ACompilerDiagnostic_IsSurfaced()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "dotnet build",
            1,
            "bash",
            "/repo/src/Thing.cs(12,9): error CS0103: The name 'foo' does not exist in the current context");

        Assert.Contains(result, item => item.Title.Contains("CS0103", StringComparison.Ordinal));
    }

    // ================================================================= inventory

    /// <summary>
    /// The table is the contract Phase 4b and Phase 5 extend, so it has to stay walkable: every
    /// entry needs a kebab-case id, a human summary, and a function. A recogniser added without
    /// them is a recogniser nobody can find.
    /// </summary>
    [Fact]
    public void EveryRecognizerHasASampleAndAProvenance()
    {
        Assert.NotEmpty(CommandErrorRecognizers.All);

        foreach (CommandErrorRecognizer recognizer in CommandErrorRecognizers.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(recognizer.Id), "every recogniser needs an id");
            Assert.False(string.IsNullOrWhiteSpace(recognizer.Summary), $"{recognizer.Id} needs a summary");
            Assert.NotNull(recognizer.Analyze);
            Assert.Equal(recognizer.Id.ToLowerInvariant(), recognizer.Id);
        }

        Assert.Equal(
            CommandErrorRecognizers.All.Count,
            CommandErrorRecognizers.All.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A recogniser must not fire on output it has nothing to do with. This is the property that
    /// makes the "ask everyone, concatenate" dispatch safe.
    /// </summary>
    [Fact]
    public async Task UnrelatedOutput_ProducesNothing()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "pytest -q",
            1,
            "bash",
            "3 failed, 41 passed in 2.13s\nFAILED tests/test_thing.py::test_one - assert 1 == 2");

        Assert.Empty(result);
    }

    private static async Task<IReadOnlyList<CommandFixSuggestion>> Analyze(
        string commandText,
        int exitCode,
        string shellKind,
        string? outputTail)
    {
        var service = new HeuristicErrorInsightService();
        return await service.AnalyzeAsync(
            new CommandFailureContext(
                CommandText: commandText,
                ExitCode: exitCode,
                ShellKind: shellKind,
                WorkingDirectory: null,
                OutputTail: outputTail,
                IsRemote: false,
                SelectedText: null),
            CancellationToken.None);
    }
}
