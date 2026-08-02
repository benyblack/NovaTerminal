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
    /// bash's DEBUG-trap preexec needs the one-shot arm/disarm flag or every statement in the
    /// user's own <c>PROMPT_COMMAND</c> is captured as the accepted command.
    /// </summary>
    [Fact]
    public void ShSnippet_KeepsTheBashDebugTrapArmDisarmGuard()
    {
        string content = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains("trap '__nova_preexec' DEBUG", content, StringComparison.Ordinal);
        Assert.Contains("__nova_command_active=1", content, StringComparison.Ordinal);
        Assert.Contains("__nova_command_active=0", content, StringComparison.Ordinal);
        Assert.Contains("__nova_*|trap*|PROMPT_COMMAND*) return ;;", content, StringComparison.Ordinal);
    }
}
