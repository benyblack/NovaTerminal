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
        Assert.Contains("PROMPT=\"$PROMPT$__nova_prompt_mark\"", script);
    }

    [Fact]
    public void BuildScript_EmitsCommandStartMarkAtTheEndOfThePrompt()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        // A is printed from precmd, i.e. before PROMPT is expanded; B has to
        // land after the last prompt cell, so it rides at the tail of PROMPT
        // wrapped in %{...%} (zero display width).
        Assert.Contains("__nova_prompt_mark=$'%{\\e]133;B\\a%}'", script);
        Assert.Contains("PROMPT=\"$PROMPT$__nova_prompt_mark\"", script);

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

        // Called once per prompt: without the containment guard a static
        // PROMPT would grow one marker per prompt cycle, forever.
        Assert.Contains("if [[ \"$PROMPT\" != *\"$__nova_prompt_mark\"* ]]; then", script);
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
