using NovaTerminal.CommandAssist.ShellIntegration.Remote;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// The shipped remote snippets (V2 Phase 2b), held to the same contract as the local
/// <c>*BootstrapBuilder</c> classes: emit the four marks plus OSC 7, append to the user's prompt
/// rather than replacing it, survive being sourced twice, and stay silent in a non-interactive
/// shell.
/// </summary>
/// <remarks>
/// <para>
/// These are static-content assertions, which is a weaker instrument than the shell-harness
/// integration tests that actually run bash and fish - and deliberately so. A snippet the user
/// pastes onto a machine we have never seen cannot be validated by running it here; what can be
/// pinned is that the properties the builders were argued into keeping are still present in their
/// ported form, so that a later edit cannot quietly drop one.
/// </para>
/// <para>
/// The rules are the same rules and the reasoning behind each lives with its builder counterpart:
/// see <c>BashBootstrapBuilderTests</c>, <c>ZshBootstrapBuilderTests</c>,
/// <c>FishBootstrapBuilderTests</c> and <c>PowerShellBootstrapBuilderTests</c>.
/// </para>
/// </remarks>
public sealed class RemoteShellIntegrationSnippetTests
{
    // ---- shipping and packaging ---------------------------------------------------------------

    /// <summary>
    /// The embedded-resource wiring in <c>NovaTerminal.CommandAssist.csproj</c> is the thing most
    /// likely to break silently: the files live outside the project directory, so a rename or a
    /// LogicalName drift produces a missing resource rather than a build error.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void EverySnippet_IsEmbeddedAndReadable(RemoteShellIntegrationShell shell)
    {
        string content = RemoteShellIntegrationSnippets.Read(shell);

        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("Nova Terminal remote shell integration", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Line endings are normalized to LF. A CRLF surviving a Windows checkout with
    /// <c>core.autocrlf=true</c> gives bash <c>$'\r': command not found</c> on every line of the
    /// file the user just pasted.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void EverySnippet_UsesLfLineEndings(RemoteShellIntegrationShell shell)
    {
        Assert.DoesNotContain("\r", RemoteShellIntegrationSnippets.Read(shell), StringComparison.Ordinal);
    }

    /// <summary>
    /// The clipboard payload has to be self-describing: a user who pastes it into
    /// <c>cat &gt; ...</c> and then closes Settings must be able to read the remaining step out of
    /// the file they just wrote.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void EverySnippet_CarriesItsOwnInstallInstructions(RemoteShellIntegrationShell shell)
    {
        string content = RemoteShellIntegrationSnippets.Read(shell);

        Assert.Contains("INSTALL", content, StringComparison.Ordinal);
        Assert.Contains(RemoteShellIntegrationSnippets.GetRemotePath(shell), content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void InstallInstructions_NameTheFileAndWhereItGoes(RemoteShellIntegrationShell shell)
    {
        string instructions = RemoteShellIntegrationSnippets.BuildInstallInstructions(shell);

        Assert.Contains(RemoteShellIntegrationSnippets.GetFileName(shell), instructions, StringComparison.Ordinal);
        Assert.Contains(RemoteShellIntegrationSnippets.GetRemotePath(shell), instructions, StringComparison.Ordinal);

        string? loader = RemoteShellIntegrationSnippets.GetLoaderLine(shell);
        if (loader != null)
        {
            Assert.Contains(loader, instructions, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// fish's <c>conf.d</c> is auto-sourced, so telling the user to add a loader line would be
    /// wrong. Pinned because the descriptor table is the single source the UI, the docs and the
    /// snippet header all read from.
    /// </summary>
    [Fact]
    public void FishNeedsNoLoaderLine_AndTheOthersDo()
    {
        Assert.Null(RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.Fish));
        Assert.NotNull(RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.BashOrZsh));
        Assert.NotNull(RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.PowerShell));
    }

    // ---- the marks ------------------------------------------------------------------------------

    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void EverySnippet_EmitsTheFullLifecycleMarkerSet(RemoteShellIntegrationShell shell)
    {
        string content = RemoteShellIntegrationSnippets.Read(shell);

        Assert.Contains("]7;", content, StringComparison.Ordinal);
        Assert.Contains("]133;A", content, StringComparison.Ordinal);
        Assert.Contains("]133;B", content, StringComparison.Ordinal);
        Assert.Contains("]133;C;", content, StringComparison.Ordinal);
        Assert.Contains("]133;D;", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The C payload is base64 even in the remote snippets, which is what lets a command containing
    /// the OSC parameter separator or a newline survive the round trip. (Phase 2b also taught the
    /// parser to accept a bare or plain-text C, but that is a tolerance for other people's
    /// integrations, not a licence for ours to be lossy.)
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh, "base64")]
    [InlineData(RemoteShellIntegrationShell.Fish, "base64")]
    [InlineData(RemoteShellIntegrationShell.PowerShell, "ToBase64String")]
    public void EverySnippet_Base64EncodesTheAcceptedCommand(RemoteShellIntegrationShell shell, string token)
    {
        Assert.Contains(token, RemoteShellIntegrationSnippets.Read(shell), StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression guard for the macOS/BSD <c>date +%s%N</c> portability bug, which matters more
    /// remotely than locally: the host on the other end of the SSH connection is exactly the one
    /// whose <c>date</c> we have not seen. bash/zsh use <c>$EPOCHREALTIME</c>; fish probes at
    /// runtime and falls back to whole seconds, so it is the one file allowed to mention
    /// <c>+%s%N</c>.
    /// </summary>
    [Fact]
    public void ShSnippet_UsesEpochRealtimeRatherThanGnuDateNanoseconds()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("EPOCHREALTIME", content, StringComparison.Ordinal);
        Assert.Contains("zsh/datetime", content, StringComparison.Ordinal);
        Assert.DoesNotContain("date +%s%N", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FishSnippet_ProbesDateAtRuntimeRatherThanAssumingGnu()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish);

        Assert.Contains("date +%s%N", content, StringComparison.Ordinal);
        Assert.Contains("string match -qr '^[0-9]+$'", content, StringComparison.Ordinal);
    }

    // ---- prompt preservation --------------------------------------------------------------------

    /// <summary>
    /// The rule every builder was argued into: the snippet may only <em>append</em> the zero-width
    /// <c>133;B</c> suffix to the prompt the user already has. A template assignment would silently
    /// replace a carefully built prompt on a machine the user has to SSH back into to repair.
    /// </summary>
    [Fact]
    public void ShSnippet_AppendsToThePromptRatherThanReplacingIt()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        // bash: appended to PS1, wrapped in \[ \] so prompt-width arithmetic is unaffected.
        Assert.Contains(@"__nova_ps1_mark='\[\e]133;B\a\]'", content, StringComparison.Ordinal);
        Assert.Contains(@"PS1=""$PS1$__nova_ps1_mark""", content, StringComparison.Ordinal);

        // zsh: appended to PROMPT, wrapped in %{...%} for the same reason.
        Assert.Contains(@"__nova_prompt_mark=$'%{\e]133;B\a%}'", content, StringComparison.Ordinal);
        Assert.Contains(@"PROMPT=""${PROMPT%$__nova_prompt_mark}$__nova_prompt_mark""", content, StringComparison.Ordinal);

        // Exactly one assignment each: no template anywhere.
        Assert.Equal(1, content.Split("PS1=\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, content.Split("PROMPT=\"", StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// bash re-applies from <c>__nova_arm</c>, the last entry in the <c>PROMPT_COMMAND</c> chain, so
    /// a theme that rewrites <c>PS1</c> from inside <c>PROMPT_COMMAND</c> cannot drop the mark. zsh
    /// strips-then-appends from <c>precmd</c>, which is idempotent <em>and</em> self-correcting -
    /// zsh has no "run last" guarantee, so a hook registered after ours can bury the mark
    /// mid-prompt.
    /// </summary>
    [Fact]
    public void ShSnippet_ReAppliesThePromptMarkEveryCycle()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        int armIndex = content.IndexOf("__nova_arm() {", StringComparison.Ordinal);
        Assert.True(armIndex > 0, "bash arm hook must exist");
        Assert.True(
            content.IndexOf("__nova_apply_ps1_mark", armIndex, StringComparison.Ordinal) > armIndex,
            "__nova_arm must re-apply the PS1 mark");

        int precmdIndex = content.IndexOf("__nova_zsh_precmd() {", StringComparison.Ordinal);
        Assert.True(precmdIndex > 0, "zsh precmd hook must exist");
        Assert.True(
            content.IndexOf("__nova_apply_prompt_mark", precmdIndex, StringComparison.Ordinal) > precmdIndex,
            "__nova_zsh_precmd must re-apply the PROMPT mark");
    }

    /// <summary>
    /// fish has no post-prompt event, so the only way to get <c>B</c> after the last prompt cell is
    /// to copy the user's <c>fish_prompt</c> aside and wrap it - and the copy is what makes a
    /// re-source infinitely recursive if it is not guarded.
    /// </summary>
    [Fact]
    public void FishSnippet_WrapsTheUserPromptBehindANotAlreadyWrappedGuard()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish);

        Assert.Contains(
            "if functions -q fish_prompt; and not functions -q __nova_user_fish_prompt",
            content,
            StringComparison.Ordinal);
        Assert.Contains("functions --copy fish_prompt __nova_user_fish_prompt", content, StringComparison.Ordinal);
        Assert.Contains("__nova_user_fish_prompt", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// PowerShell's equivalent: capture the original <c>prompt</c> once, behind both a
    /// scope guard and a sentinel grep, so a second dot-source cannot capture our own wrapper as
    /// "the original" and recurse forever.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_CapturesTheOriginalPromptBehindTwoGuards()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.Contains("Get-Variable -Name 'NovaOriginalPrompt' -Scope Script", content, StringComparison.Ordinal);
        Assert.Contains("-notlike '*__nova_prompt_wrapper*'", content, StringComparison.Ordinal);
        Assert.Contains("# __nova_prompt_wrapper", content, StringComparison.Ordinal);
        Assert.Contains("(& $script:NovaOriginalPrompt) -join ''", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// PSReadLine is not present in every PowerShell environment, and on a remote host it is less
    /// likely to be than locally. Probing keeps the snippet from taking the shell down; the fallback
    /// is losing only the C payload.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_ProbesForPsReadLineBeforeBindingEnter()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.Contains(
            "if (Get-Command Set-PSReadLineKeyHandler -ErrorAction SilentlyContinue) {",
            content,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Unlike the injected local bootstrap, the remote snippet is dot-sourced into the user's own
    /// profile, so it must not set a global preference that outlives it.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_DoesNotChangeTheUsersErrorActionPreference()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.DoesNotContain("$ErrorActionPreference =", content, StringComparison.Ordinal);
    }

    // ---- idempotence and bail-outs ---------------------------------------------------------------

    /// <summary>
    /// Two guards, not one. The load guard covers the ordinary re-source; the per-mechanism
    /// not-already-wrapped guards cover the case the load guard cannot see - a fresh scope, or a
    /// framework that rebuilt the hook chain after we installed into it.
    /// </summary>
    [Fact]
    public void ShSnippet_GuardsAgainstBeingSourcedTwice()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("__nova_shell_integration_loaded", content, StringComparison.Ordinal);

        // bash: the PROMPT_COMMAND chain must not gain a second copy of our hooks.
        Assert.Contains("*__nova_precmd*) ;;", content, StringComparison.Ordinal);

        // zsh: the hook arrays must not gain a second copy either.
        Assert.Contains(@"*"" __nova_zsh_precmd ""*) ;;", content, StringComparison.Ordinal);
        Assert.Contains(@"*"" __nova_zsh_preexec ""*) ;;", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FishSnippet_GuardsAgainstBeingSourcedTwice()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish);

        Assert.Contains("not set -q __nova_shell_integration_loaded", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// An OSC written into a non-interactive shell's stdout corrupts an <c>scp</c> or <c>rsync</c>
    /// stream, and unlike the injected bootstrap (which only ever runs in a shell Nova launched
    /// interactively) a snippet in <c>~/.bashrc</c> or <c>conf.d</c> will be sourced by those.
    /// </summary>
    [Fact]
    public void ShSnippet_BailsOutOfNonInteractiveShells()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("case \"$-\" in", content, StringComparison.Ordinal);
        Assert.Contains("*i*) ;;", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FishSnippet_BailsOutOfNonInteractiveShells()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish);

        Assert.Contains("status is-interactive", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// fish's <c>exit</c> in a sourced file exits the shell rather than the file, so the whole
    /// snippet has to sit inside one guard block. This pins the shape - an early-exit refactor would
    /// close the user's terminal on every non-interactive fish.
    /// </summary>
    [Fact]
    public void FishSnippet_UsesAGuardBlockRatherThanAnEarlyExit()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish);

        Assert.DoesNotContain("\n    exit 0", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\nexit 0", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// bash and zsh share one file so the user does not have to know which of the two they landed
    /// on. The dispatch is on the shell's own version variable rather than on <c>$0</c>, which lies
    /// under <c>exec -a</c> and login shells.
    /// </summary>
    [Fact]
    public void ShSnippet_DispatchesOnTheShellsOwnVersionVariable()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("if [ -n \"${BASH_VERSION:-}\" ]; then", content, StringComparison.Ordinal);
        Assert.Contains("elif [ -n \"${ZSH_VERSION:-}\" ]; then", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// bash's DEBUG-trap preexec needs the arm/disarm flag or every statement in the user's own
    /// <c>PROMPT_COMMAND</c> is captured as the accepted command.
    /// </summary>
    /// <remarks>
    /// Only <c>__nova_*</c> is filtered by name. <c>trap*</c> and <c>PROMPT_COMMAND*</c> used to be
    /// filtered too, which silently dropped any user command starting with either word - and was
    /// never what kept our own hooks out anyway. The behaviour that does is asserted for real in
    /// <c>RemoteBashSnippetIntegrationTests</c>.
    /// </remarks>
    [Fact]
    public void ShSnippet_KeepsTheBashDebugTrapArmDisarmGuard()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("trap '__nova_preexec' DEBUG", content, StringComparison.Ordinal);
        Assert.Contains("__nova_command_active=1", content, StringComparison.Ordinal);
        Assert.Contains("__nova_command_active=0", content, StringComparison.Ordinal);
        Assert.Contains("__nova_*) return ;;", content, StringComparison.Ordinal);
        Assert.DoesNotContain("trap*|", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>__nova_precmd</c> raises the busy flag as its first act, which is what keeps an empty
    /// Enter - where no user command ran, so nothing else raised it - from capturing the first
    /// entry of the user's own <c>PROMPT_COMMAND</c> chain as a phantom accepted command.
    /// </summary>
    [Fact]
    public void ShSnippet_RaisesTheBusyFlagAtTheTopOfPrecmd()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        int precmdIndex = content.IndexOf("__nova_precmd() {", StringComparison.Ordinal);
        Assert.True(precmdIndex > 0, "bash precmd hook must exist");

        int raiseIndex = content.IndexOf("__nova_command_active=1", precmdIndex, StringComparison.Ordinal);
        int completionIndex = content.IndexOf("__nova_emit_completion", precmdIndex, StringComparison.Ordinal);

        Assert.True(raiseIndex > precmdIndex, "__nova_precmd must raise the busy flag");
        Assert.True(raiseIndex < completionIndex, "the busy flag must be raised before anything else runs");
    }

    /// <summary>
    /// <c>$BASH_COMMAND</c> is the first simple command of the line, not the line, so the accepted
    /// command is read back out of history instead - bash-preexec's approach.
    /// </summary>
    [Fact]
    public void ShSnippet_ReadsTheAcceptedLineFromHistoryRatherThanBashCommand()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("HISTTIMEFORMAT='' builtin history 1", content, StringComparison.Ordinal);
        // ...with BASH_COMMAND kept only as the fallback for a shell with history off.
        Assert.Contains("|| __nova_line=\"$BASH_COMMAND\"", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>EPOCHREALTIME</c> is a zsh <em>parameter</em>, so the module feature prefix is <c>+p:</c>.
    /// <c>zmodload -F</c> with an unknown feature name fails, and with the error swallowed the
    /// module never loads: durations silently degrade from milliseconds to whole seconds, which is
    /// exactly the kind of failure a static assertion is good at pinning.
    /// </summary>
    [Fact]
    public void ShSnippet_LoadsEpochRealtimeAsAParameterNotABuiltin()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("zmodload -F zsh/datetime +p:EPOCHREALTIME", content, StringComparison.Ordinal);
        Assert.DoesNotContain("+b:EPOCHREALTIME", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// zsh's exit-code snapshot has to run FIRST. <c>$?</c> inside a precmd hook is the status of
    /// whatever ran immediately before it, and for an appended hook that is the previous precmd
    /// hook rather than the user's command - so on any setup with another precmd registered
    /// (oh-my-zsh, powerlevel10k, vcs_info) the reported exit code was that hook's. The prompt-mark
    /// hook still has to run last, hence two hooks at opposite ends of the array.
    /// </summary>
    [Fact]
    public void ShSnippet_SnapshotsTheZshExitCodeFromAPrependedHook()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains(
            "precmd_functions=(__nova_zsh_status_snapshot \"${precmd_functions[@]}\")",
            content,
            StringComparison.Ordinal);
        Assert.Contains("__nova_emit_completion \"$__nova_last_status\"", content, StringComparison.Ordinal);
        Assert.Contains("precmd_functions+=(__nova_zsh_precmd)", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// fish's <c>math</c> defaults to scale 6, so an unqualified division prints
    /// <c>1780000000123.456787</c> - and <c>AnsiParser</c> parses the OSC 133;D duration field with
    /// <c>long.TryParse</c>, so a fractional value is not a rounded duration, it is no duration.
    /// </summary>
    [Fact]
    public void FishSnippet_ForcesIntegerMath()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish);

        Assert.DoesNotContain("math \"$raw", content, StringComparison.Ordinal);
        Assert.Contains("math -s0 \"$raw / 1000000\"", content, StringComparison.Ordinal);
        Assert.Contains("math -s0 $now_ms - $__nova_command_start_ms", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The path part of a <c>file://</c> URI needs exactly one leading slash. On Linux/macOS pwsh
    /// the path already has one, and <c>"host" + "/" + path</c> produced <c>file://host//home/you</c>.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_NormalizesTheOsc7Path()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.Contains("if (-not $novaPath.StartsWith('/')) { $novaPath = '/' + $novaPath }", content, StringComparison.Ordinal);
        Assert.DoesNotContain("file://$([System.Net.Dns]::GetHostName())/$cwd", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The remote snippet and the local bootstrap emit the same OSC 7 URI, character for character
    /// (PR #293 review, blocker 3).
    /// </summary>
    /// <remarks>
    /// They already drifted once - #289 fixed the leading-slash bug in the snippet and left the builder
    /// alone, so a locally instrumented pwsh and a snippet-instrumented one reported cwd differently. The
    /// hostname authority is gone from both: <c>file://HOST/C:/x</c> resolves to the UNC share
    /// <c>\\HOST\C:\x</c>, and nothing ever read the hostname.
    /// </remarks>
    [Fact]
    public void PowerShellSnippet_EmitsTheSameOsc7UriAsTheLocalBootstrap()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);
        string builderScript = NovaTerminal.CommandAssist.ShellIntegration.PowerShell.PowerShellBootstrapBuilder.BuildScript();

        foreach (string line in new[]
                 {
                     "$novaSegments = ((Get-Location).Path -replace '\\\\', '/') -split '/'",
                     "$novaPath = (($novaSegments | ForEach-Object { [Uri]::EscapeDataString($_) -replace '%3A', ':' }) -join '/')",
                     "if (-not $novaPath.StartsWith('/')) { $novaPath = '/' + $novaPath }",
                     "Write-NovaSequence \"]7;file://$novaPath\"",
                 })
        {
            Assert.Contains(line, content, StringComparison.Ordinal);
            Assert.Contains(line, builderScript, StringComparison.Ordinal);
        }

        // The old emission's two ingredients, matched as code rather than as words - both are named in
        // the snippet's own comments explaining why they are gone.
        Assert.DoesNotContain("[Uri]::EscapeUriString(", content, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.Net.Dns]::GetHostName()", content, StringComparison.Ordinal);
        Assert.DoesNotContain("[Uri]::EscapeUriString(", builderScript, StringComparison.Ordinal);
    }

    /// <summary>
    /// Binding Enter unconditionally to <c>AcceptLine</c> clobbers whatever the user had there -
    /// <c>AcceptOrInsertNewline</c> is the pwsh default in recent PSReadLine versions, and
    /// hard-coding over it breaks multi-line editing. A named PSReadLine function is delegated to;
    /// a custom scriptblock cannot be recovered from PSReadLine at all and is documented instead.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_DelegatesToTheExistingEnterHandler()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.Contains("Get-PSReadLineKeyHandler -Chord 'Enter'", content, StringComparison.Ordinal);
        Assert.Contains("$script:NovaEnterFallback", content, StringComparison.Ordinal);
        Assert.Contains("-match '^[A-Za-z]+$'", content, StringComparison.Ordinal);
        Assert.Contains("SCRIPTBLOCK", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every variable the snippet leaves behind is Nova-scoped. It is dot-sourced into the user's
    /// own <c>$PROFILE</c>, so a bare <c>$esc</c> / <c>$bel</c> lands in their session.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_ScopesItsVariableNames()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.DoesNotContain("$esc = ", content, StringComparison.Ordinal);
        Assert.DoesNotContain("$bel = ", content, StringComparison.Ordinal);
        Assert.Contains("$script:NovaEsc = [char]27", content, StringComparison.Ordinal);
        Assert.Contains("$script:NovaBel = [char]7", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header used to claim PSReadLine costs you only the C text. It costs D as well: the Enter
    /// chord is where the clock starts, so without PSReadLine there is no accepted command to time
    /// and no completion mark at all.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_TellsTheTruthAboutWhatMissingPsReadLineCosts()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.DoesNotContain("you still get A / B / D", content, StringComparison.Ordinal);
        Assert.Contains("WHAT YOU LOSE WITHOUT PSReadLine", content, StringComparison.Ordinal);
        Assert.Contains("no `133;C` command text AND no", content, StringComparison.Ordinal);
    }
}
