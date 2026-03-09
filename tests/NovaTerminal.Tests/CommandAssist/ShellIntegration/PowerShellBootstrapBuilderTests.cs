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
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void BuildScript_DoesNotHardcodeDefaultPromptRendering()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.DoesNotContain("'PS ' + (Get-Location) + '> '", script);
    }

    [Fact]
    public void BuildScript_WrapsExistingPromptImplementation()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("Get-Command prompt", script);
        Assert.Contains("& $script:NovaOriginalPrompt", script);
    }

    [Fact]
    public void BuildScript_OnlyEmitsCompletionWhenAcceptedCommandIsActive()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.DoesNotContain("if ($global:LASTEXITCODE -ne $null -or $?)", script);
        Assert.Contains("if ($script:NovaCommandStart -eq $null) { return }", script);
    }

    [Fact]
    public void BuildScript_DoesNotWriteAcceptedOrStartedMarkersInsidePsReadLineHistoryHandler()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.DoesNotContain("Write-NovaSequence \"]133;C;", script);
        Assert.DoesNotContain("Write-NovaSequence ']133;B'", script);
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
