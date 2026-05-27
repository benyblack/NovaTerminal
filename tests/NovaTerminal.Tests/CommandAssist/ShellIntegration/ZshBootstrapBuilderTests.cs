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
        Assert.DoesNotContain("PROMPT=", script);
        Assert.DoesNotContain("PS1=", script);
    }

    [Fact]
    public void BuildScript_Base64EncodesAcceptedCommandPayload()
    {
        string script = ZshBootstrapBuilder.BuildScript();

        Assert.Contains("base64", script);
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
