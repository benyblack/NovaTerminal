using System;
using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests.Pty;

/// <summary>
/// The post-launch init used to be written to %TEMP%\nova_init_{guid}.ps1 and invoked with
/// <c>&amp; '&lt;path&gt;'</c>. Running a .ps1 is blocked under Windows' default Restricted
/// execution policy, so every PowerShell pane without shell integration greeted the user with a
/// red UnauthorizedAccess error. Sending the statements as input instead means no file is loaded,
/// so no policy applies — and it retires the %TEMP% leak that #107 was about.
/// </summary>
public sealed class PowerShellPostLaunchInitTests
{
    [Fact]
    public void BuildInjection_ReferencesNoScriptFile()
    {
        string injection = PowerShellPostLaunchInit.BuildInjection();

        Assert.DoesNotContain(".ps1", injection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nova_init", injection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInjection_IsASingleSubmission()
    {
        // Typed into a live shell, so an embedded newline would submit a partial
        // statement and leave the rest sitting at the prompt.
        string injection = PowerShellPostLaunchInit.BuildInjection();
        string body = injection.TrimEnd('\r', '\n');

        Assert.DoesNotContain('\n', body);
        Assert.DoesNotContain('\r', body);
        Assert.EndsWith("\r", injection, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInjection_StillSetsUtf8AndClearsTheInjectedText()
    {
        string injection = PowerShellPostLaunchInit.BuildInjection();

        // UTF-8 is why this exists at all; Clear-Host is what hides the command
        // we just typed into the user's shell.
        Assert.Contains("UTF8", injection, StringComparison.Ordinal);
        Assert.Contains("Clear-Host", injection, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInjection_StillPrintsTheBannerNoLogoSuppressed()
    {
        string injection = PowerShellPostLaunchInit.BuildInjection();

        Assert.Contains("Windows PowerShell", injection, StringComparison.Ordinal);
        Assert.Contains("Microsoft Corporation", injection, StringComparison.Ordinal);
    }
}
