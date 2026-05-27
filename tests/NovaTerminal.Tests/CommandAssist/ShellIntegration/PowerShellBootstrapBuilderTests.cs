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
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void BuildScript_EmitsAcceptedCommandMarkerFromEnterKeyHandler()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("Set-PSReadLineKeyHandler", script);
        Assert.Contains("-Chord 'Enter'", script);
        Assert.Contains("GetBufferState", script);
        Assert.Contains("AcceptLine", script);
    }

    [Fact]
    public void BuildScript_GuardsPsReadLineUsageBehindCmdletProbe()
    {
        // Regression guard: minimal PowerShell environments don't ship
        // PSReadLine. With $ErrorActionPreference = 'Stop' set at the top
        // of the bootstrap, calling Set-PSReadLineKeyHandler unconditionally
        // would terminate startup. The probe pattern below silently skips
        // the key handler install when the cmdlet isn't available.
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("Get-Command Set-PSReadLineKeyHandler -ErrorAction SilentlyContinue", script);
    }

    [Fact]
    public void BuildScript_Base64EncodesAcceptedCommandPayload()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("[Convert]::ToBase64String", script);
        Assert.Contains("[Text.Encoding]::UTF8", script);
    }

    [Fact]
    public void BuildScript_ClearsAcceptedCommandStateAfterCompletionMarker()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("$script:NovaAcceptedCommandText = $null", script);
        Assert.Contains("$script:NovaCommandStart = $null", script);
    }

    [Fact]
    public void BuildScript_DoesNotRegisterOnIdleEngineEvent()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.DoesNotContain("PowerShell.OnIdle", script);
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
    public void BuildScript_ReportsFailedCmdletEvenWhenLastExitCodeIsStaleZero()
    {
        // Regression guard: PowerShell cmdlets set $? to $false on failure
        // but do not touch $LASTEXITCODE. If a successful external command
        // ran first ($LASTEXITCODE=0), then a cmdlet fails ($?=$false), the
        // bootstrap must NOT report exit 0 -- it must use $LASTEXITCODE
        // only when nonzero, falling back to 1 otherwise.
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("$lastExitCode -ne $null -and $lastExitCode -ne 0", script);
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
