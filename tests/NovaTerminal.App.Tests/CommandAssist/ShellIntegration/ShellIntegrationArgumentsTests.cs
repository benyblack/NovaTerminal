using System;
using System.IO;
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
    private const string BootstrapDir = @"C:\Users\x\AppData\Local\NovaTerminal\command-assist";

    private static string OurBootstrap => Path.Combine(BootstrapDir, "command-assist-bootstrap.ps1");

    private static string Encode(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    [Fact]
    public void StripInjected_RemovesAStaleBootstrapFileArgument()
    {
        string stale = $"-NoLogo -NoExit -File {OurBootstrap}";

        string cleaned = ShellIntegrationArguments.StripInjected(stale, BootstrapDir);

        Assert.DoesNotContain("command-assist-bootstrap.ps1", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-File", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-NoLogo", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void StripInjected_RemovesOurEncodedBootstrap()
    {
        string encoded = Encode(
            NovaTerminal.CommandAssist.ShellIntegration.PowerShell.PowerShellBootstrapBuilder.BuildScript());

        string cleaned = ShellIntegrationArguments.StripInjected(
            $"-NoLogo -NoExit -EncodedCommand {encoded}", BootstrapDir);

        Assert.DoesNotContain("-EncodedCommand", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(encoded, cleaned, StringComparison.Ordinal);
    }

    // Greptile P1 on #368. Split-on-space plus a single-space rejoin rewrote every restored
    // command line, not just ones we had injected into — so a quoted value containing repeated
    // spaces came back altered and the shell ran something the user never configured.
    [Theory]
    [InlineData("-Command \"a  b\"")]
    [InlineData("-NoLogo   -NoExit")]
    [InlineData("  -NoLogo ")]
    [InlineData(@"-File ""C:\my  scripts\run.ps1""")]
    public void StripInjected_ReturnsTheCommandLineVerbatimWhenNothingWasInjected(string args)
    {
        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, BootstrapDir));
    }

    // Greptile P1 on #368. A suffix-only check claimed any file with our name, wherever it lived.
    [Fact]
    public void StripInjected_KeepsAUserScriptThatMerelySharesTheBootstrapFileName()
    {
        // Same file name, a directory we do not own. It is the user's script.
        const string userArgs = @"-NoLogo -File C:\mine\command-assist-bootstrap.ps1";

        string cleaned = ShellIntegrationArguments.StripInjected(userArgs, BootstrapDir);

        Assert.Equal(userArgs, cleaned);
    }

    [Fact]
    public void StripInjected_KeepsAUsersOwnScript()
    {
        const string userArgs = @"-NoLogo -File C:\work\my-profile.ps1";

        Assert.Equal(userArgs, ShellIntegrationArguments.StripInjected(userArgs, BootstrapDir));
    }

    [Fact]
    public void StripInjected_KeepsAUsersOwnEncodedCommand()
    {
        string userEncoded = Encode("Write-Host 'hello'");
        string args = $"-EncodedCommand {userEncoded}";

        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, BootstrapDir));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("-NoLogo", "-NoLogo")]
    public void StripInjected_LeavesOrdinaryArgumentsAlone(string? input, string expected)
    {
        Assert.Equal(expected, ShellIntegrationArguments.StripInjected(input, BootstrapDir));
    }

    [Fact]
    public void StripInjected_ToleratesAMalformedEncodedCommand()
    {
        const string args = "-EncodedCommand not-base64!!";

        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, BootstrapDir));
    }

    [Fact]
    public void StripInjected_WithoutAKnownBootstrapDirectory_KeepsEverything()
    {
        // No directory to compare against means no way to prove a -File is ours, and
        // guessing would launch a shell without the user's script.
        string args = $"-File {OurBootstrap}";

        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, bootstrapDirectory: null));
    }
}
