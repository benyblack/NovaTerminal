using System;
using System.Text;
using NovaTerminal.Shell;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using Xunit;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// The bootstrap used to be launched with <c>-File</c>, which PowerShell gates behind the
/// execution policy. Stock Windows client default is <c>Restricted</c>, which blocks every
/// script file — so every PowerShell tab opened with a red UnauthorizedAccess error and
/// Command Assist silently did not work. Reported from a real first-run install.
///
/// <c>-EncodedCommand</c> is not policy-gated (nothing is loaded from disk) and, unlike
/// <c>-ExecutionPolicy Bypass</c>, it does not relax the policy for anything the user
/// subsequently runs in that session, and it is not overridden by Group Policy.
/// </summary>
public sealed class PowerShellBootstrapExecutionPolicyTests
{
    private static PowerShellShellIntegrationProvider NewProvider()
        => new(() => BootstrapTestDirectory.ForCaller());

    [Fact]
    public void CreateLaunchPlan_DoesNotUseFile_BecauseExecutionPolicyBlocksIt()
    {
        ShellIntegrationLaunchPlan plan = NewProvider().CreateLaunchPlan(
            shellCommand: "powershell.exe",
            shellArguments: "-NoLogo",
            workingDirectory: @"C:\repo");

        Assert.True(plan.IsIntegrated);
        Assert.DoesNotContain("-File", plan.ShellArguments!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateLaunchPlan_PassesTheBootstrapAsAnEncodedCommand()
    {
        ShellIntegrationLaunchPlan plan = NewProvider().CreateLaunchPlan(
            shellCommand: "powershell.exe",
            shellArguments: null,
            workingDirectory: null);

        Assert.Contains("-EncodedCommand", plan.ShellArguments!, StringComparison.Ordinal);

        string decoded = DecodeEncodedCommand(plan.ShellArguments!);

        // Proves it is really our bootstrap that got encoded, not an empty string or
        // a path. The sentinel is what the script uses to detect its own wrapper.
        Assert.Contains("__nova_prompt_wrapper", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateLaunchPlan_PutsEncodedCommandLast_SoNothingIsSwallowedIntoIt()
    {
        ShellIntegrationLaunchPlan plan = NewProvider().CreateLaunchPlan(
            shellCommand: "powershell.exe",
            shellArguments: "-NoLogo",
            workingDirectory: null);

        // PowerShell treats every token after -EncodedCommand as part of the command,
        // so any argument placed after it is silently absorbed rather than applied.
        string args = plan.ShellArguments!;
        int encodedAt = args.IndexOf("-EncodedCommand", StringComparison.Ordinal);
        string tail = args[(encodedAt + "-EncodedCommand".Length)..].Trim();

        Assert.DoesNotContain(" ", tail);
    }

    [Fact]
    public void CreateLaunchPlan_StillKeepsNoLogoAndNoExit()
    {
        ShellIntegrationLaunchPlan plan = NewProvider().CreateLaunchPlan(
            shellCommand: "powershell.exe",
            shellArguments: null,
            workingDirectory: null);

        Assert.Contains("-NoLogo", plan.ShellArguments!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-NoExit", plan.ShellArguments!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateLaunchPlan_StillWritesTheScriptToDisk_SoTheRemoteInstallerAndDebuggingKeepWorking()
    {
        ShellIntegrationLaunchPlan plan = NewProvider().CreateLaunchPlan(
            shellCommand: "powershell.exe",
            shellArguments: null,
            workingDirectory: null);

        Assert.NotNull(plan.BootstrapScriptPath);
        Assert.True(System.IO.File.Exists(plan.BootstrapScriptPath));
    }

    [Theory]
    [InlineData("-Command Get-Date")]
    [InlineData("-EncodedCommand RwBlAHQALQBEAGEAdABlAA==")]
    public void CreateLaunchPlan_WhenUserAlreadySuppliesACommand_DoesNotClaimIntegration(string userArgs)
    {
        // Appending our own -EncodedCommand after the user's would be swallowed into
        // theirs; prepending would swallow theirs into ours. Neither is recoverable,
        // so stay out of the way — the same bail-out -File already had.
        ShellIntegrationLaunchPlan plan = NewProvider().CreateLaunchPlan(
            shellCommand: "powershell.exe",
            shellArguments: userArgs,
            workingDirectory: null);

        Assert.False(plan.IsIntegrated);
        Assert.Equal(userArgs, plan.ShellArguments);
        Assert.Null(plan.BootstrapScriptPath);
    }

    private static string DecodeEncodedCommand(string shellArguments)
    {
        int at = shellArguments.IndexOf("-EncodedCommand", StringComparison.Ordinal);
        Assert.True(at >= 0, "no -EncodedCommand in " + shellArguments);

        string base64 = shellArguments[(at + "-EncodedCommand".Length)..].Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(base64));
    }
}
