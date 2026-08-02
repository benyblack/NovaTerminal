using NovaTerminal.CommandAssist.ShellIntegration.Bash;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class BashBootstrapBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public BashBootstrapBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_bash_bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildScript_ContainsStructuredLifecycleMarkers()
    {
        string script = BashBootstrapBuilder.BuildScript();

        Assert.Contains("]7;", script);
        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void BuildScript_EmitsCommandStartMarkAtTheEndOfPs1()
    {
        string script = BashBootstrapBuilder.BuildScript();

        // A is printed from PROMPT_COMMAND, which bash runs *before* it
        // expands and prints PS1. B marks the opposite edge -- the first cell
        // of the user's input -- so it can only ride at the tail of PS1, and
        // must be wrapped in \[ \] so bash does not count it as prompt width.
        Assert.Contains("__nova_ps1_mark='\\[\\e]133;B\\a\\]'", script);
        Assert.Contains("PS1=\"$PS1$__nova_ps1_mark\"", script);

        // Appended, never assigned from a template: the only PS1 assignment
        // in the script is the append form above.
        Assert.Equal(1, script.Split("PS1=", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildScript_ReAppliesPs1MarkAfterUserPromptCommandRuns()
    {
        string script = BashBootstrapBuilder.BuildScript();

        // Themes like starship/oh-my-posh rewrite PS1 from inside
        // PROMPT_COMMAND, which would drop a one-shot suffix. __nova_arm is
        // the last entry in the PROMPT_COMMAND chain, so re-applying from
        // there gets the final word without changing the chain string (and
        // therefore without disturbing the DEBUG-trap arm/disarm ordering).
        int armIndex = script.IndexOf("__nova_arm() {", StringComparison.Ordinal);
        int applyIndex = script.IndexOf("    __nova_apply_ps1_mark", armIndex, StringComparison.Ordinal);
        int clearIndex = script.IndexOf("    __nova_command_active=0", armIndex, StringComparison.Ordinal);

        Assert.True(applyIndex > armIndex, "__nova_arm must re-apply the PS1 mark");
        Assert.True(clearIndex > applyIndex, "the active flag must still be cleared last");

        // Idempotent: a static PS1 must not accumulate one marker per prompt.
        Assert.Contains("*\"$__nova_ps1_mark\"*) ;;", script);
    }

    [Fact]
    public void BuildScript_InstallsPromptCommandAndDebugTrap()
    {
        string script = BashBootstrapBuilder.BuildScript();

        Assert.Contains("PROMPT_COMMAND", script);
        Assert.Contains("trap", script);
        Assert.Contains("DEBUG", script);
    }

    [Fact]
    public void BuildScript_ArmsActiveFlagAfterUserPromptCommandFinishes()
    {
        // Locks in the PROMPT_COMMAND race fix: __nova_arm runs LAST in
        // PROMPT_COMMAND (suffix), and __nova_emit_completion no longer
        // clears the active flag itself. Without these two invariants,
        // DEBUG fires from the user's own PROMPT_COMMAND helpers would
        // masquerade as accepted commands.
        string script = BashBootstrapBuilder.BuildScript();

        Assert.Contains("__nova_arm() {", script);
        Assert.Contains("__nova_command_active=0", script);
        Assert.Contains("__nova_precmd; __nova_arm", script);
        Assert.Contains("__nova_precmd; $PROMPT_COMMAND; __nova_arm", script);
    }

    [Fact]
    public void BuildScript_SourcesUserBashrcIfPresent()
    {
        string script = BashBootstrapBuilder.BuildScript();

        Assert.Contains("~/.bashrc", script);
    }

    [Fact]
    public void BuildScript_Base64EncodesAcceptedCommandPayload()
    {
        string script = BashBootstrapBuilder.BuildScript();

        // The C marker payload must be base64-encoded so multiline commands
        // survive transit through the VT byte stream.
        Assert.Contains("base64", script);
    }

    [Fact]
    public void BuildScript_UsesBsdPortableTimingNotGnuDateNanoseconds()
    {
        string script = BashBootstrapBuilder.BuildScript();

        // Regression guard for the macOS/BSD `date +%s%N` portability bug.
        // The bootstrap must prefer $EPOCHREALTIME (bash 5+) and not use
        // `+%s%N` directly because BSD `date` outputs literal "%N", which
        // breaks subsequent arithmetic.
        Assert.Contains("EPOCHREALTIME", script);
        Assert.DoesNotContain("date +%s%N", script);
    }

    [Fact]
    public void WriteScript_WritesBootstrapIntoRequestedDirectory()
    {
        string path = BashBootstrapBuilder.WriteScript(_tempRoot);

        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".bash", path, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
