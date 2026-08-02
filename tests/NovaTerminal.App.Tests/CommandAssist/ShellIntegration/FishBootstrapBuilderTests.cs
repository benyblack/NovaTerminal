using NovaTerminal.CommandAssist.ShellIntegration.Fish;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class FishBootstrapBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public FishBootstrapBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_fish_bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildScript_ContainsStructuredLifecycleMarkers()
    {
        string script = FishBootstrapBuilder.BuildScript();

        Assert.Contains("]7;", script);
        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void BuildScript_EmitsCommandStartMarkAfterTheUserPromptRenders()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // The fish_prompt EVENT fires before the prompt function runs, so it
        // can only carry A. fish has no post-prompt event, so B is emitted by
        // re-defining fish_prompt as "copy of the user's prompt, then B" --
        // the copy keeps the user's prompt output byte-for-byte.
        Assert.Contains("functions --copy fish_prompt __nova_user_fish_prompt", script);

        int redefIndex = script.IndexOf("function fish_prompt\n", StringComparison.Ordinal);
        Assert.True(redefIndex > 0, "fish_prompt must be re-defined around the copied original");

        int originalCallIndex = script.IndexOf("    __nova_user_fish_prompt", redefIndex, StringComparison.Ordinal);
        int markIndex = script.IndexOf("133;B", redefIndex, StringComparison.Ordinal);
        Assert.True(originalCallIndex > redefIndex, "the copied user prompt must run first");
        Assert.True(markIndex > originalCallIndex, "B must be emitted after the user's prompt output");
    }

    [Fact]
    public void BuildScript_SkipsPromptWrappingWhenFishPromptIsUnavailable()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // Degradation guard, mirroring the PSReadLine probe in the PowerShell
        // bootstrap: if fish_prompt cannot be resolved we emit A only rather
        // than replacing the user's prompt with a synthesized one.
        Assert.Contains("if functions -q fish_prompt", script);
    }

    [Fact]
    public void BuildScript_UsesNativeFishEventHandlers()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // fish's native event hooks are fish_preexec and fish_prompt
        // (the latter fires once per prompt cycle, after the previous
        // command finishes). Using these keeps integration prompt-owner
        // friendly: we don't override fish_prompt itself, we just hook it.
        Assert.Contains("fish_preexec", script);
        Assert.Contains("fish_prompt", script);
        Assert.Contains("function", script);
    }

    [Fact]
    public void BuildScript_Base64EncodesAcceptedCommandPayload()
    {
        string script = FishBootstrapBuilder.BuildScript();

        Assert.Contains("base64", script);
    }

    [Fact]
    public void BuildScript_DetectsBsdDateAndFallsBackToSecondPrecision()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // Regression guard for the macOS/BSD `date +%s%N` portability bug.
        // The bootstrap detects whether `+%N` produced digits and falls
        // back to plain `date +%s` * 1000 when it didn't.
        Assert.Contains("string match", script);
        Assert.Contains("date +%s", script);
    }

    [Fact]
    public void WriteScript_WritesConfigFishInsideFishConfigSubdirectory()
    {
        // Fish reads config from $XDG_CONFIG_HOME/fish/config.fish. The
        // bootstrap must live inside a per-shell <root>/fish/config.fish
        // layout so XDG_CONFIG_HOME can simply point at <root>.
        string path = FishBootstrapBuilder.WriteScript(_tempRoot);

        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("config.fish", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fish", Path.GetDirectoryName(path)!, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
