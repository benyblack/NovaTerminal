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
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
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
