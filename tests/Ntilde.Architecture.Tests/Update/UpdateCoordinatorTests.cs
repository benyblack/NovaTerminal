using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Update;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// These tests live in the Architecture project, not beside the rest of the app tests, because
/// this project is in the gating unit loop that <c>ci.yml</c> and <c>release.yml</c> run
/// (VT, Rendering, Architecture, Platform, McpServer) while <c>App.Tests</c> is not - CI marks
/// that job green via <c>continue-on-error</c>. <see cref="UpdateCoordinator"/> is pure policy
/// (no Avalonia, no Windows, no network), and this project already has a ProjectReference to
/// <c>NovaTerminal.App</c>, so nothing is given up by putting the update rules somewhere a
/// regression actually blocks a release. The Velopack-backed implementation's tests stay in
/// <c>App.Tests</c>.
/// </summary>
public class UpdateCoordinatorTests
{
    // Hoisted out of the Assert.Equal calls below: this project is built with
    // TreatWarningsAsErrors, which promotes CA1861 (constant array arguments are re-allocated
    // on every call) to an error. Matches ProjectFileLayeringTests' existing style.
    private static readonly string[] Version050Only = ["0.5.0"];

    // Same CA1861 reasoning as Version050Only above.
    private static readonly string[] Version120Only = ["1.2.0"];

    // Every check below passes TestContext.Current.CancellationToken rather than relying on the
    // coordinator's default. Also a TreatWarningsAsErrors consequence: xUnit1051 is an error in
    // this project, where in App.Tests it was only advisory.
    private sealed class FakeUpdateService : IUpdateService
    {
        public bool IsSupported { get; set; } = true;
        public UpdateAvailability Result { get; set; } = new(false, null);
        public Exception? Throw { get; set; }
        public int CheckCount { get; private set; }
        public int ApplyCount { get; private set; }

        public Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct)
        {
            CheckCount++;
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }

        public void ApplyAndRestart() => ApplyCount++;
    }

    private sealed class Harness
    {
        public FakeUpdateService Service { get; } = new();
        public bool AutomaticChecksEnabled { get; set; } = true;
        public List<string> Ready { get; } = [];
        public List<string> Log { get; } = [];

        public UpdateCoordinator Build() => new(
            Service,
            () => AutomaticChecksEnabled,
            version => Ready.Add(version),
            message => Log.Add(message));
    }

    [Fact]
    public async Task Not_a_velopack_install_reports_unsupported_and_never_checks()
    {
        var harness = new Harness();
        harness.Service.IsSupported = false;

        var outcome = await harness.Build().RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Unsupported, outcome);
        Assert.Equal(0, harness.Service.CheckCount);
        Assert.Empty(harness.Ready);
    }

    [Fact]
    public async Task Automatic_check_is_skipped_when_the_setting_is_off()
    {
        var harness = new Harness { AutomaticChecksEnabled = false };

        var outcome = await harness.Build().RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Disabled, outcome);
        Assert.Equal(0, harness.Service.CheckCount);
    }

    [Fact]
    public async Task Manual_check_runs_even_when_the_setting_is_off()
    {
        var harness = new Harness { AutomaticChecksEnabled = false };

        var outcome = await harness.Build().RunManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpToDate, outcome);
        Assert.Equal(1, harness.Service.CheckCount);
    }

    [Fact]
    public async Task No_update_available_raises_nothing()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(false, null);

        var coordinator = harness.Build();
        var outcome = await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpToDate, outcome);
        Assert.Empty(harness.Ready);
        Assert.False(coordinator.IsUpdateStaged);
    }

    [Fact]
    public async Task A_throwing_service_is_logged_and_swallowed()
    {
        var harness = new Harness();
        harness.Service.Throw = new InvalidOperationException("github is down");

        var coordinator = harness.Build();
        var outcome = await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, outcome);
        Assert.Empty(harness.Ready);
        Assert.False(coordinator.IsUpdateStaged);
        Assert.Contains(harness.Log, m => m.Contains("github is down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_available_update_is_staged_and_announced_once()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "0.5.0");

        var coordinator = harness.Build();
        var outcome = await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateReady, outcome);
        Assert.True(coordinator.IsUpdateStaged);
        Assert.Equal("0.5.0", coordinator.StagedVersion);
        Assert.Equal(Version050Only, harness.Ready);
    }

    [Fact]
    public async Task A_second_check_does_not_re_announce_an_already_staged_update()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "0.5.0");
        var coordinator = harness.Build();

        await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);
        var second = await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateReady, second);
        Assert.Equal(Version050Only, harness.Ready);
    }

    [Fact]
    public async Task Applying_a_staged_update_delegates_to_the_service()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "0.5.0");
        var coordinator = harness.Build();
        await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);

        coordinator.ApplyStagedUpdate();

        Assert.Equal(1, harness.Service.ApplyCount);
    }

    [Fact]
    public void Applying_with_nothing_staged_is_a_no_op()
    {
        var harness = new Harness();

        harness.Build().ApplyStagedUpdate();

        Assert.Equal(0, harness.Service.ApplyCount);
    }

    /// <summary>
    /// IUpdateService's contract (see UpdateAvailability's doc comment) requires a non-null
    /// Version whenever HasUpdate is true. Because IsUpdateStaged is defined as
    /// StagedVersion != null, a service that violates the contract with (true, null) would
    /// otherwise stage the empty string, report the check as ready, and announce an update
    /// with a blank version number. This must fail loudly instead - and, critically, must not
    /// clobber whatever was legitimately staged before the violation. A fresh harness would
    /// leave StagedVersion null either way, so this stages a real version first: only then
    /// does asserting it survives actually pin "untouched" rather than "still its initial
    /// value."
    /// </summary>
    [Fact]
    public async Task An_update_available_with_a_null_version_is_a_contract_violation_not_a_staged_update()
    {
        var harness = new Harness();
        harness.Service.Result = new UpdateAvailability(true, "1.2.0");
        var coordinator = harness.Build();
        await coordinator.RunAutomaticCheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.2.0", coordinator.StagedVersion);

        harness.Service.Result = new UpdateAvailability(true, null);
        var outcome = await coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, outcome);
        Assert.Equal(Version120Only, harness.Ready);
        Assert.True(coordinator.IsUpdateStaged);
        Assert.Equal("1.2.0", coordinator.StagedVersion);
        Assert.Contains(harness.Log, m => m.Contains("contract", StringComparison.Ordinal));
    }
}
