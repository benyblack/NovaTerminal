using System;
using System.Collections.Generic;
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
    public void Apply_is_a_no_op_when_unsupported()
    {
        var service = new VelopackUpdateService(VelopackUpdateService.DefaultRepoUrl, _ => { });

        // Must not throw, and must certainly not restart the test host.
        service.ApplyAndRestart();
    }
}
