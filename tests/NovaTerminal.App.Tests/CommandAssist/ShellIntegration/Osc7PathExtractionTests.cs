using System;
using System.Linq;
using NovaTerminal.CommandAssist.ShellIntegration.PowerShell;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// What <c>AnsiParser</c> hands Command Assist as a working directory for a given OSC 7 payload.
/// </summary>
/// <remarks>
/// <para>
/// <strong>PR #293 review, blocker 3.</strong> The cwd is a load-bearing input: path suggestions list it,
/// and the ranking engine scores history rows by whether they were run in it. A bogus cwd therefore does
/// not fail loudly - it makes path suggestions silently empty and history ranking silently wrong, which
/// is how <c>file://HOST/C:%5CUsers%5Cyou</c> survived four milestones.
/// </para>
/// <para>
/// Both halves are covered here. The emitter's *exact* current emission is reconstructed and fed through
/// the parser (the review asked for the old one to be measured before anything was changed - the answer
/// was <c>\\legoinb\C:\Users\behna\projects</c>, reproduced below as a regression case), and the older
/// emissions are kept passing because a remote host still runs whatever snippet it was given.
/// </para>
/// </remarks>
public sealed class Osc7PathExtractionTests
{
    private static string? Extract(string payload)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        string? cwd = null;
        parser.OnWorkingDirectoryChanged = value => cwd = value;

        parser.Process("\u001b]7;" + payload + "\u0007");
        return cwd;
    }

    // ---------------------------------------------------------------- the new emission

    [Theory]
    [InlineData("file:///C:/Users/behna/projects", @"C:\Users\behna\projects")]
    [InlineData("file:///D:/projects/nova2/.worktrees", @"D:\projects\nova2\.worktrees")]
    [InlineData("file:///C:/", @"C:\")]
    [InlineData("file:///C:/Users/behna/my%20dir", @"C:\Users\behna\my dir")]
    [InlineData("file:///C:/Users/behna/a%23b", @"C:\Users\behna\a#b")]
    [InlineData("file:///C:/Users/behna/100%25%20done", @"C:\Users\behna\100% done")]
    public void AuthoritylessWindowsUri_DecodesToAWindowsPath(string payload, string expected)
    {
        Assert.Equal(expected, Extract(payload));
    }

    /// <summary>POSIX paths are untouched, which is what the pre-existing OSC 7 test pins.</summary>
    [Theory]
    [InlineData("file:///tmp/project", "/tmp/project")]
    [InlineData("file:///home/you/src", "/home/you/src")]
    [InlineData("file:///home/you/my%20notes", "/home/you/my notes")]
    public void AuthoritylessPosixUri_DecodesToAPosixPath(string payload, string expected)
    {
        Assert.Equal(expected, Extract(payload));
    }

    // ---------------------------------------------------------------- older emissions

    /// <summary>
    /// The exact payload the shipped bootstrap produced on Windows, with the hostname in the authority
    /// and the separators percent-escaped. It used to come back as <c>\\HOST\C:\Users\...</c>.
    /// </summary>
    [Fact]
    public void LegacyEmissionWithLocalHostnameAndEscapedBackslashes_DecodesToAWindowsPath()
    {
        string payload = $"file://{Environment.MachineName}/C:%5CUsers%5Cbehna%5Cprojects";

        Assert.Equal(@"C:\Users\behna\projects", Extract(payload));
    }

    /// <summary>
    /// The remote snippet's post-#289 emission: forward slashes and one leading slash, but still a
    /// hostname authority. Fixed on Linux by #289; still a UNC on Windows until this change.
    /// </summary>
    [Fact]
    public void LegacyEmissionWithLocalHostnameAndForwardSlashes_DecodesToAWindowsPath()
    {
        string payload = $"file://{Environment.MachineName}/C:/Users/behna/projects";

        Assert.Equal(@"C:\Users\behna\projects", Extract(payload));
    }

    /// <summary>
    /// bash/zsh/fish put the local hostname in the authority as a matter of convention, and their paths
    /// are POSIX.
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void LocalAuthorityWithAPosixPath_DropsTheAuthority(string host)
    {
        Assert.Equal("/home/you/src", Extract($"file://{host}/home/you/src"));
    }

    /// <summary>An FQDN whose first label is this machine is still this machine.</summary>
    [Fact]
    public void FullyQualifiedLocalAuthority_IsTreatedAsLocal()
    {
        Assert.Equal("/home/you", Extract($"file://{Environment.MachineName}.lan/home/you"));
    }

    // ---------------------------------------------------------------- what is deliberately unchanged

    /// <summary>
    /// A genuinely foreign authority keeps the UNC reading. Second-guessing a remote host's path layout
    /// is not the parser's job, and this is the one case where the UNC is what the user means.
    /// </summary>
    [Fact]
    public void ForeignAuthority_StillReadsAsAUncPath()
    {
        Assert.Equal(@"\\fileserver\share\dir", Extract("file://fileserver/share/dir"));
    }

    /// <summary>A payload that is not a URI at all - some shells emit a bare path - is passed through.</summary>
    [Fact]
    public void NonUriPayload_IsPassedThroughVerbatim()
    {
        Assert.Equal("/var/log", Extract("/var/log"));
    }

    // ---------------------------------------------------------------- the two ends, joined

    /// <summary>
    /// The end-to-end guard: reproduce the bootstrap's emission for a known path using the same
    /// transformation the script performs, and assert the parser recovers the path exactly.
    /// </summary>
    /// <remarks>
    /// The C# here mirrors the PowerShell in <c>PowerShellBootstrapBuilder.Write-NovaPwd</c>. Mirrored
    /// rather than executed because spawning pwsh from a unit test is a integration-suite cost; the script
    /// text itself is pinned by <c>PowerShellBootstrapBuilderTests</c>, and this asserts that what that
    /// text computes is what the parser wants.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Users\behna\projects\nova2")]
    [InlineData(@"D:\projects\nova2\.worktrees\phase3b")]
    [InlineData(@"C:\Users\behna\my dir")]
    [InlineData(@"C:\Users\behna\a#b")]
    [InlineData("/home/you/src")]
    public void PowerShellBootstrap_Osc7Emission_RoundTripsThroughTheParser(string cwd)
    {
        string payload = MirrorWriteNovaPwd(cwd);

        Assert.Equal(cwd, Extract(payload));
    }

    /// <summary>
    /// The bootstrap's OSC 7 payload for <paramref name="cwd"/>: flip separators, escape per segment,
    /// restore ':', ensure one leading slash, no authority.
    /// </summary>
    private static string MirrorWriteNovaPwd(string cwd)
    {
        string path = string.Join(
            '/',
            cwd.Replace('\\', '/')
                .Split('/')
                .Select(segment => Uri.EscapeDataString(segment).Replace("%3A", ":", StringComparison.Ordinal)));

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return "file://" + path;
    }

    /// <summary>
    /// And the mirror is a mirror: the transformation above is the one the emitted script performs, so a
    /// change to either without the other fails here.
    /// </summary>
    [Fact]
    public void TheMirroredTransformation_MatchesTheEmittedScript()
    {
        string script = PowerShellBootstrapBuilder.BuildScript();

        Assert.Contains("(Get-Location).Path -replace '\\\\', '/'", script);
        Assert.Contains("-split '/'", script);
        Assert.Contains("[Uri]::EscapeDataString($_) -replace '%3A', ':'", script);
        Assert.Contains("-join '/'", script);
        Assert.Contains("Write-NovaSequence \"]7;file://$novaPath\"", script);
    }
}
