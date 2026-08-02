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
    public void BuildScript_AppendsCommandStartMarkToTheReturnedPromptString()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        // Anything the prompt function *writes* lands before the prompt text,
        // because the host prints the returned string afterwards. A is written
        // (prompt start); B has to be appended to the returned string so it
        // lands on the first cell of the user's input.
        Assert.Contains("return \"$novaPromptText$([char]27)]133;B$([char]7)\"", script);

        int promptReadyIndex = script.IndexOf("    Write-NovaPromptReady", StringComparison.Ordinal);
        int markIndex = script.IndexOf("]133;B", StringComparison.Ordinal);
        Assert.True(promptReadyIndex > 0 && markIndex > promptReadyIndex,
            "the B mark must be produced after the A mark within the prompt function");
    }

    [Fact]
    public void BuildScript_KeepsUserPromptOutputWhenAppendingCommandStartMark()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        // The wrapped original prompt still supplies the prompt text; we only
        // concatenate the zero-width mark onto whatever it produced.
        Assert.Contains("$novaPromptText = if ($script:NovaOriginalPrompt -ne $null) {", script);
        Assert.Contains("(& $script:NovaOriginalPrompt) -join ''", script);
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
    public void BuildScript_CapturesTheOriginalPromptAtMostOnce()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        // Running the bootstrap a second time in a session it already initialized
        // (a second -File pass, a user dot-source, `exec pwsh`) must not capture
        // OUR wrapper as "the original prompt" -- the wrapper would then invoke
        // itself forever. Two guards, because a second -File pass gets a fresh
        // script scope in which the first guard alone cannot see the earlier capture.
        Assert.Contains(
            "if (-not (Get-Variable -Name 'NovaOriginalPrompt' -Scope Script -ErrorAction SilentlyContinue)) {",
            script);
        Assert.Contains("if ($null -eq $script:NovaOriginalPrompt -and", script);
        Assert.Contains("-notlike '*__nova_prompt_wrapper*'", script);

        // The sentinel the second guard looks for must actually be inside the
        // wrapper body, or the check silently never fires.
        int promptIndex = script.IndexOf("function Global:prompt {", StringComparison.Ordinal);
        int sentinelIndex = script.IndexOf("    # __nova_prompt_wrapper", StringComparison.Ordinal);
        Assert.True(promptIndex >= 0 && sentinelIndex > promptIndex,
            "the wrapper sentinel must live inside the Global:prompt body");

        // The pre-fix shape: an unconditional reset followed by an unconditional
        // capture. Its presence would re-enable the recursion.
        Assert.DoesNotContain(
            "$script:NovaOriginalPrompt = $null\n$script:NovaPromptCommand = Get-Command prompt",
            script);
    }

    [Fact]
    public void BuildScript_DoesNotAccumulatePromptMarks_AcrossPromptCycles()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        // The pwsh analogue of the bash accumulation test. PowerShell cannot grow a
        // marker per cycle the way a PS1/PROMPT *string* can: the mark is concatenated
        // onto the value the wrapper RETURNS, and the wrapper composes that value fresh
        // from $script:NovaOriginalPrompt on every call. So the invariants to pin are
        // (a) exactly one place emits B, in the return expression, and (b) the wrapper
        // never mutates the stored original.
        Assert.Equal(1, CountOccurrences(script, "]133;B"));
        Assert.Contains("return \"$novaPromptText$([char]27)]133;B$([char]7)\"", script);

        // The only assignment to the captured original is the guarded capture above --
        // nothing inside the prompt function writes to it.
        int promptIndex = script.IndexOf("function Global:prompt {", StringComparison.Ordinal);
        Assert.True(promptIndex > 0);
        Assert.DoesNotContain(
            "$script:NovaOriginalPrompt =",
            script[promptIndex..]);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
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
