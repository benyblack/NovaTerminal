using System;
using System.Text;
using NovaTerminal.CommandAssist.ShellIntegration;
using Xunit;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// A pane persisted the arguments it was *launched* with, which included the shell-integration
/// bootstrap we injected. On the next launch the provider saw its own `-File` in the incoming
/// arguments, took the "the user supplied a script" bail-out, and passed the stale command line
/// through unchanged — so integration silently stopped and the old bootstrap path was launched
/// forever. Reported after a 0.5.0 → 0.6.0 update: the restored session carried 0.5.0's arguments.
///
/// This strips what we injected so a stored command line cannot be mistaken for the user's own.
/// </summary>
public sealed class ShellIntegrationArgumentsTests
{
    private static string Encode(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    [Fact]
    public void StripInjected_RemovesAStaleBootstrapFileArgument()
    {
        string stale = @"-NoLogo -NoExit -File C:\Users\x\AppData\Local\NovaTerminal\command-assist\command-assist-bootstrap.ps1";

        string cleaned = ShellIntegrationArguments.StripInjected(stale);

        Assert.DoesNotContain("command-assist-bootstrap.ps1", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-File", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripInjected_RemovesOurEncodedBootstrap()
    {
        string encoded = Encode(NovaTerminal.CommandAssist.ShellIntegration.PowerShell.PowerShellBootstrapBuilder.BuildScript());

        string cleaned = ShellIntegrationArguments.StripInjected($"-NoLogo -NoExit -EncodedCommand {encoded}");

        Assert.DoesNotContain("-EncodedCommand", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(encoded, cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void StripInjected_KeepsAUsersOwnScript()
    {
        // The whole point of the bail-out is to stay out of the way of a user script.
        // Stripping it would launch a shell the user did not ask for.
        const string userArgs = @"-NoLogo -File C:\work\my-profile.ps1";

        string cleaned = ShellIntegrationArguments.StripInjected(userArgs);

        Assert.Equal(userArgs, cleaned);
    }

    [Fact]
    public void StripInjected_KeepsAUsersOwnEncodedCommand()
    {
        string userEncoded = Encode("Write-Host 'hello'");

        string cleaned = ShellIntegrationArguments.StripInjected($"-EncodedCommand {userEncoded}");

        Assert.Contains(userEncoded, cleaned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("-NoLogo", "-NoLogo")]
    public void StripInjected_LeavesOrdinaryArgumentsAlone(string? input, string expected)
    {
        Assert.Equal(expected, ShellIntegrationArguments.StripInjected(input));
    }

    [Fact]
    public void StripInjected_ToleratesAMalformedEncodedCommand()
    {
        // Not valid base64. Decoding must not throw and take the pane's launch with it.
        const string args = "-EncodedCommand not-base64!!";

        string cleaned = ShellIntegrationArguments.StripInjected(args);

        Assert.Equal(args, cleaned);
    }
}
