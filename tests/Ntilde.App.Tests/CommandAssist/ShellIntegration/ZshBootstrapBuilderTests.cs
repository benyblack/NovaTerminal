using NovaTerminal.CommandAssist.ShellIntegration.Zsh;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class ZshBootstrapBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public ZshBootstrapBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_zsh_bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildScript_ContainsStructuredLifecycleMarkers()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        Assert.Contains("]7;", script);
        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void BuildScript_UsesNativeZshPrecmdAndPreexec()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        Assert.Contains("precmd_functions", script);
        Assert.Contains("preexec_functions", script);
    }

    [Fact]
    public void BuildScript_PreservesPromptOwnership()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        // The bootstrap must not overwrite PROMPT/PS1 with our own template;
        // it can only emit OSC markers around the user's existing prompt.
        // The single permitted PROMPT assignment is the zero-width OSC 133;B
        // suffix, which appends to whatever the user's prompt already is.
        Assert.DoesNotContain("PS1=", script);
        Assert.Equal(
            1,
            script.Split("PROMPT=", StringSplitOptions.None).Length - 1);
        Assert.Contains("PROMPT=\"${PROMPT%$__nova_prompt_mark}$__nova_prompt_mark\"", script);
    }

    [Fact]
    public void BuildScript_EmitsCommandStartMarkAtTheEndOfThePrompt()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        // A is printed from precmd, i.e. before PROMPT is expanded; B has to
        // land after the last prompt cell, so it rides at the tail of PROMPT
        // wrapped in %{...%} (zero display width).
        Assert.Contains("__nova_prompt_mark=$'%{\\e]133;B\\a%}'", script);
        Assert.Contains("PROMPT=\"${PROMPT%$__nova_prompt_mark}$__nova_prompt_mark\"", script);

        // ...and it is (re)applied from precmd so prompt frameworks that
        // reassign PROMPT every cycle cannot drop it.
        int precmdIndex = script.IndexOf("__nova_precmd() {", StringComparison.Ordinal);
        int applyIndex = script.IndexOf("    __nova_apply_prompt_mark", precmdIndex, StringComparison.Ordinal);
        Assert.True(applyIndex > precmdIndex, "precmd must re-apply the prompt mark");
    }

    [Fact]
    public void BuildScript_AppliesPromptMarkIdempotently()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        // Called once per prompt, so it must not grow PROMPT by a marker per
        // cycle. Strip-then-append rather than skip-if-present: a "contains?"
        // guard is idempotent but not self-correcting -- unlike bash, zsh gives
        // us no way to be the last precmd hook, so a hook registered after ours
        // can append to PROMPT and strand our mark mid-prompt, where it would
        // report the input cell several columns early. ${PROMPT%pattern} trims
        // only a *trailing* match and is a no-op when absent, so the mark is
        // re-seated at the true tail every cycle.
        Assert.Contains("PROMPT=\"${PROMPT%$__nova_prompt_mark}$__nova_prompt_mark\"", script);
        Assert.DoesNotContain("if [[ \"$PROMPT\" != *\"$__nova_prompt_mark\"* ]]; then", script);
    }

    [Fact]
    public void BuildScript_DoesNotAccumulatePromptMarks_AcrossSimulatedPromptCycles()
    {
        // Builder-level stand-in for the real-shell accumulation test bash has
        // (zsh is not installed on Windows CI). Models what
        // __nova_apply_prompt_mark does to PROMPT over repeated precmd cycles,
        // including the "a later hook appended something" case that the old
        // containment guard could not repair.
        const string mark = "%{]133;B%}";
        static string Apply(string prompt, string m) =>
            (prompt.EndsWith(m, StringComparison.Ordinal) ? prompt[..^m.Length] : prompt) + m;

        string prompt = "user@host %# ";
        for (int i = 0; i < 10; i++)
        {
            prompt = Apply(prompt, mark);
        }

        Assert.Equal("user@host %# " + mark, prompt);
        Assert.Equal(1, prompt.Split(mark).Length - 1);

        // A prompt framework appends after us. The next cycle must put a mark back
        // at the true tail; the containment guard would have seen "already present"
        // and left the only mark buried several columns early. (An orphaned earlier
        // mark is harmless -- B is re-emitted per prompt and the parser hands the
        // consumer the newest one.)
        prompt += "$ ";
        prompt = Apply(prompt, mark);
        Assert.EndsWith(mark, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_Base64EncodesAcceptedCommandPayload()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        Assert.Contains("base64", script);
    }

    [Fact]
    public void BuildScript_UsesZshDatetimeModuleNotGnuDateNanoseconds()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        // Regression guard for the macOS/BSD `date +%s%N` portability bug.
        // The bootstrap loads zsh/datetime so $EPOCHREALTIME is available
        // and avoids `+%s%N`, which leaves a literal "%N" on BSD `date`.
        Assert.Contains("zmodload", script);
        Assert.Contains("zsh/datetime", script);
        Assert.Contains("EPOCHREALTIME", script);
        Assert.DoesNotContain("date +%s%N", script);
    }

    /// <summary>
    /// EPOCHREALTIME is a zsh <em>parameter</em>, so the feature prefix is <c>+p:</c>. With
    /// <c>+b:</c> (the builtin namespace, which has no EPOCHREALTIME in it) <c>zmodload -F</c>
    /// fails, the error is swallowed by the <c>2&gt;/dev/null || true</c>, the module never loads,
    /// and every duration silently degrades to a whole number of seconds. Nothing else in the
    /// script would look wrong.
    /// </summary>
    [Fact]
    public void BuildScript_LoadsEpochRealtimeAsAParameterNotABuiltin()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        Assert.Contains("zmodload -F zsh/datetime +p:EPOCHREALTIME", script);
        Assert.DoesNotContain("+b:EPOCHREALTIME", script);
    }

    /// <summary>
    /// The exit code reported on <c>133;D</c> has to come from a hook that runs FIRST. <c>$?</c>
    /// inside a precmd hook is the status of whatever ran immediately before it, and our hook is
    /// appended - so on any configuration with another precmd registered (oh-my-zsh,
    /// powerlevel10k, a vcs_info hook) <c>$?</c> was that hook's status, i.e. almost always 0, and
    /// every failing command was reported as a success. The prompt-mark hook still has to run last,
    /// so the two jobs are split across two hooks at opposite ends of the array.
    /// </summary>
    [Fact]
    public void BuildScript_SnapshotsTheExitCodeFromAPrependedHook()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        Assert.Contains("precmd_functions=(__nova_status_snapshot \"${precmd_functions[@]}\")", script);
        Assert.Contains("__nova_last_status=$?", script);
        Assert.Contains("local exit=$__nova_last_status", script);

        // ...and the mark-applying hook is still the appended one.
        int prependIndex = script.IndexOf("precmd_functions=(__nova_status_snapshot", StringComparison.Ordinal);
        int appendIndex = script.IndexOf("precmd_functions+=(__nova_precmd)", StringComparison.Ordinal);
        Assert.True(prependIndex > 0 && appendIndex > prependIndex);
    }

    [Fact]
    public void WriteScript_WritesBootstrapAsZshrcInsideZshSubdirectory()
    {
        // zsh sources $ZDOTDIR/.zshrc on interactive startup, so the bootstrap
        // file must be named exactly ".zshrc" and live in its own directory
        // that ZDOTDIR will point at -- otherwise the rest of the shared
        // command-assist directory (.ps1, .bash, ...) would also be visible.
        string path = ZshBootstrapBuilder.WriteScript(_tempRoot);

        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".zshrc", path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(_tempRoot, Path.GetDirectoryName(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
