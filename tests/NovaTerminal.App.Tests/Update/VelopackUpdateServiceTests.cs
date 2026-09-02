using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Update;

namespace NovaTerminal.AppTests.Update;

public class VelopackUpdateServiceTests
{
    /// <summary>
    /// The test host is never a Velopack install, on any OS. This is the guard that keeps
    /// portable-zip, winget and dev runs from ever reaching the network or showing update UI,
    /// so it is worth pinning even though it looks tautological here.
    /// </summary>
    [Fact]
    public void Is_not_supported_when_the_process_is_not_a_velopack_install()
    {
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, _ => { });

        Assert.False(service.IsSupported);
    }

    [Fact]
    public async Task Check_reports_no_update_when_unsupported_instead_of_throwing()
    {
        var log = new List<string>();
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, log.Add);

        var availability = await service.CheckAndDownloadAsync(CancellationToken.None);

        Assert.False(availability.HasUpdate);
        Assert.Null(availability.Version);

        // The class doc comment's guarantee is that unsupported hosts never reach the network
        // or show update UI - an empty log is the observable proof that an unsupported check
        // produced no update chatter at all, not merely a negative result.
        Assert.Empty(log);
    }

    [Fact]
    public void Apply_does_nothing_when_no_update_has_been_downloaded()
    {
        var log = new List<string>();
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, log.Add);

        // Must not throw, and must certainly not restart the test host.
        service.ApplyAndRestart();

        // Named for what it actually pins: ApplyAndRestart branches on whether a download
        // completed, not on IsSupported, and nothing has been downloaded here. The empty log is
        // the observable proof that it returned early instead of reaching Velopack's
        // apply-and-restart path — which would have logged "Applying update and restarting."
        // first, and then terminated this test host.
        Assert.Empty(log);
    }

    // Channel resolution is a pure function taking isLinux and architecture explicitly,
    // rather than reading RuntimeInformation directly, so these cases are assertable on any
    // CI leg regardless of the host OS and CPU. That is the whole reason for the seam.
    [Theory]
    [InlineData(true, Architecture.X64, "linux-x64")]
    [InlineData(true, Architecture.Arm64, "linux-arm64")]
    public void Resolves_a_per_architecture_channel_on_linux(
        bool isLinux, Architecture architecture, string expected)
    {
        Assert.Equal(expected, VelopackUpdateService.ResolveExplicitChannel(isLinux, architecture));
    }

    /// <summary>
    /// Null means "let Velopack use the channel this release was packed with". Windows and
    /// macOS have shipped installed clients against their platform-default channels (win, osx)
    /// since #91, so returning anything but null off Linux would repoint existing installs at
    /// a feed that does not exist.
    /// </summary>
    [Theory]
    [InlineData(false, Architecture.X64)]
    [InlineData(false, Architecture.Arm64)]
    public void Resolves_no_explicit_channel_off_linux(bool isLinux, Architecture architecture)
    {
        Assert.Null(VelopackUpdateService.ResolveExplicitChannel(isLinux, architecture));
    }

    /// <summary>
    /// An architecture we publish no feed for must degrade to "no update available", not to a
    /// wrong feed and not to a throw. Null falls back to the packed default, which finds
    /// nothing on a channel we never published.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void Resolves_no_explicit_channel_for_unpublished_architectures(Architecture architecture)
    {
        Assert.Null(VelopackUpdateService.ResolveExplicitChannel(true, architecture));
    }
}
