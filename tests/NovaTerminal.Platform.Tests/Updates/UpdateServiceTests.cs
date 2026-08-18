using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using NovaTerminal.Platform.Updates;

namespace NovaTerminal.Platform.Tests.Updates;

/// <summary>
/// <see cref="UpdateService"/> is the only part of the auto-update path with unit tests, which
/// is the entire reason <see cref="IVelopackUpdater"/> exists: the real Velopack binding needs a
/// network, an installed app and an AOT publish, none of which belong in a unit suite. These
/// tests drive a hand-rolled fake rather than Moq -- a three-member interface does not justify
/// adding a mocking dependency to this project, and the fake records enough to assert on.
/// </summary>
public sealed class UpdateServiceTests
{
    private sealed class FakeUpdater : IVelopackUpdater
    {
        private readonly string? _staged;
        private readonly Exception? _throws;

        public FakeUpdater(string? staged = null, bool installed = true, Exception? throws = null)
        {
            _staged = staged;
            IsInstalled = installed;
            _throws = throws;
        }

        public bool IsInstalled { get; }

        public int CheckCallCount { get; private set; }

        public int ApplyCallCount { get; private set; }

        public Task<string?> CheckAndStageAsync()
        {
            CheckCallCount++;
            if (_throws is not null)
            {
                return Task.FromException<string?>(_throws);
            }

            return Task.FromResult(_staged);
        }

        public Task ApplyAndRestartAsync()
        {
            ApplyCallCount++;
            return Task.CompletedTask;
        }
    }

    private static UpdateService Create(FakeUpdater updater, out List<string> log)
    {
        List<string> captured = [];
        log = captured;
        return new UpdateService(updater, captured.Add);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdateStaged_SetsUpdateReadyWithVersion()
    {
        UpdateService svc = Create(new FakeUpdater(staged: "1.2.3"), out _);

        await svc.CheckAsync();

        Assert.True(svc.UpdateReady);
        Assert.Equal("1.2.3", svc.AvailableVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenUpToDate_LeavesUpdateNotReady()
    {
        UpdateService svc = Create(new FakeUpdater(staged: null), out _);

        await svc.CheckAsync();

        Assert.False(svc.UpdateReady);
        Assert.Null(svc.AvailableVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenNotInstalled_SkipsCheckEntirely()
    {
        // The portable zip and every dev run land here. Velopack has no install directory to
        // update, so touching the network at all would be wasted work on every single launch.
        FakeUpdater updater = new(staged: "1.2.3", installed: false);
        UpdateService svc = Create(updater, out _);

        await svc.CheckAsync();

        Assert.False(svc.UpdateReady);
        Assert.Equal(0, updater.CheckCallCount);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdaterThrows_SwallowsAndStaysNotReady()
    {
        UpdateService svc = Create(
            new FakeUpdater(throws: new HttpRequestException("offline")),
            out _);

        await svc.CheckAsync();

        Assert.False(svc.UpdateReady);
        Assert.Null(svc.AvailableVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdaterThrows_LogsTheFailure()
    {
        // Swallowing is required -- a failed update check must never break startup -- but
        // swallowing silently would make an always-failing feed invisible. Injecting the log
        // sink instead of calling a static logger is what makes this assertable.
        UpdateService svc = Create(
            new FakeUpdater(throws: new HttpRequestException("offline")),
            out List<string> log);

        await svc.CheckAsync();

        Assert.Single(log);
        Assert.Contains("offline", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdateStaged_RaisesUpdateReadyChanged()
    {
        UpdateService svc = Create(new FakeUpdater(staged: "9.9.9"), out _);
        bool raised = false;
        svc.UpdateReadyChanged += () => raised = true;

        await svc.CheckAsync();

        Assert.True(raised);
    }

    [Fact]
    public async Task CheckAsync_WhenUpToDate_DoesNotRaiseUpdateReadyChanged()
    {
        UpdateService svc = Create(new FakeUpdater(staged: null), out _);
        bool raised = false;
        svc.UpdateReadyChanged += () => raised = true;

        await svc.CheckAsync();

        Assert.False(raised);
    }

    [Fact]
    public async Task ApplyAsync_WhenReady_CallsUpdaterApply()
    {
        FakeUpdater updater = new(staged: "1.2.3");
        UpdateService svc = Create(updater, out _);
        await svc.CheckAsync();

        await svc.ApplyAsync();

        Assert.Equal(1, updater.ApplyCallCount);
    }

    [Fact]
    public async Task ApplyAsync_WhenNotReady_DoesNothing()
    {
        FakeUpdater updater = new(staged: null);
        UpdateService svc = Create(updater, out _);
        await svc.CheckAsync();

        await svc.ApplyAsync();

        Assert.Equal(0, updater.ApplyCallCount);
    }

    [Fact]
    public void Constructor_WithNullUpdater_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateService(null!, _ => { }));
    }
}
