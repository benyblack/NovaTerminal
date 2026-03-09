using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class PowerShellBootstrapBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public PowerShellBootstrapBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildScript_ContainsExpectedLifecycleMarkers()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("]7;", script);
        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void WriteScript_WritesBootstrapIntoRequestedDirectory()
    {
        string path = PowerShellBootstrapBuilder.WriteScript(_tempRoot);

        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".ps1", path, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
