using System;
using System.Collections.Generic;
using System.IO;
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

        private readonly Exception? _applyThrows;

        public FakeUpdater(
            string? staged = null,
            bool installed = true,
            Exception? throws = null,
            Exception? applyThrows = null)
        {
            _staged = staged;
            IsInstalled = installed;
            _throws = throws;
            _applyThrows = applyThrows;
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
            return _applyThrows is not null
                ? Task.FromException(_applyThrows)
                : Task.CompletedTask;
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
    public async Task ApplyAsync_WhenUpdaterThrows_DoesNotPropagate()
    {
        // The title-bar button handler is `async void`, so an exception escaping here reaches
        // Avalonia's dispatcher unhandled and can take down a window full of live panes. The
        // palette caller discards the task instead, which would fail silently. Neither is an
        // acceptable outcome for a failed update, so ApplyAsync swallows exactly like CheckAsync.
        UpdateService svc = Create(
            new FakeUpdater(staged: "1.2.3", applyThrows: new IOException("file in use")),
            out _);
        await svc.CheckAsync();

        await svc.ApplyAsync(); // must not throw
    }

    [Fact]
    public async Task ApplyAsync_WhenUpdaterThrows_LogsTheFailure()
    {
        UpdateService svc = Create(
            new FakeUpdater(staged: "1.2.3", applyThrows: new IOException("file in use")),
            out List<string> log);
        await svc.CheckAsync();

        await svc.ApplyAsync();

        Assert.Single(log);
        Assert.Contains("file in use", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_WhenApplyFails_StaysReadySoTheUserCanRetry()
    {
        // The package is still staged on disk after a failed apply, so clearing UpdateReady
        // would hide a usable update behind a transient file lock.
        UpdateService svc = Create(
            new FakeUpdater(staged: "1.2.3", applyThrows: new IOException("file in use")),
            out _);
        await svc.CheckAsync();

        await svc.ApplyAsync();

        Assert.True(svc.UpdateReady);
        Assert.Equal("1.2.3", svc.AvailableVersion);
    }

    [Fact]
    public void Constructor_WithNullUpdater_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateService(null!, _ => { }));
    }
}
