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
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
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
