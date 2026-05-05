using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class RemoteFilesSidebarViewModelTests
{
    [Fact]
    public async Task OpenAsync_LoadsInitialPath_AndDisablesDownloadUntilSelectionExists()
    {
        var service = new FakeRemoteDirectoryBrowserService("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        });

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(
            profileId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            initialPath: "/srv",
            CancellationToken.None);

        Assert.True(viewModel.IsOpen);
        Assert.Equal("/srv", viewModel.CurrentPath);
        Assert.False(viewModel.CanDownloadSelected);
    }

    [Fact]
    public async Task SelectingEntry_EnablesDownload()
    {
        var service = new FakeRemoteDirectoryBrowserService("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        });

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);

        Assert.True(viewModel.CanDownloadSelected);
    }

    [Fact]
    public async Task OpenAsync_MapsModifiedMetadataToEntryViewModels()
    {
        var service = new FakeRemoteDirectoryBrowserService("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
            {
                ModifiedAtUtc = new DateTime(2026, 5, 4, 20, 15, 0, DateTimeKind.Utc)
            }
        });

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        RemoteFilesSidebarEntryViewModel entry = Assert.Single(viewModel.Entries);
        Assert.Equal("May 04", entry.ModifiedDisplayText);
    }

    [Fact]
    public async Task OpenAsync_UsesPlaceholderWhenModifiedMetadataIsMissing()
    {
        var service = new FakeRemoteDirectoryBrowserService("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        });

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        RemoteFilesSidebarEntryViewModel entry = Assert.Single(viewModel.Entries);
        Assert.Equal("-", entry.ModifiedDisplayText);
    }

    [Fact]
    public async Task NavigateIntoSelectedDirectoryAsync_ThenNavigateBackAsync_ReturnsToPreviousPath()
    {
        var service = new FakeRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("/srv", new[]
            {
                new RemoteSidebarEntry("logs", "/srv/logs", true)
            }),
            RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()),
            RemoteSidebarListingResult.Success("/srv", Array.Empty<RemoteSidebarEntry>()));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);

        await viewModel.NavigateIntoSelectedDirectoryAsync();

        Assert.Equal("/srv/logs", viewModel.CurrentPath);
        Assert.True(viewModel.CanNavigateBack);

        await viewModel.NavigateBackAsync();

        Assert.Equal("/srv", viewModel.CurrentPath);
        Assert.False(viewModel.CanNavigateBack);
        Assert.Equal(new[] { "/srv", "/srv/logs", "/srv" }, service.RequestedPaths);
    }

    [Fact]
    public async Task NavigateBackAsync_WhenLoadFails_KeepsBackStackAvailableForRetry()
    {
        var service = new FakeRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("/srv", new[]
            {
                new RemoteSidebarEntry("logs", "/srv/logs", true)
            }),
            RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()),
            RemoteSidebarListingResult.Failure("/srv", "permission denied"),
            RemoteSidebarListingResult.Success("/srv", Array.Empty<RemoteSidebarEntry>()));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);
        await viewModel.NavigateIntoSelectedDirectoryAsync();

        await viewModel.NavigateBackAsync();

        Assert.True(viewModel.CanNavigateBack);
        Assert.Equal("permission denied", viewModel.ErrorMessage);

        await viewModel.NavigateBackAsync();

        Assert.Equal("/srv", viewModel.CurrentPath);
        Assert.False(viewModel.CanNavigateBack);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(new[] { "/srv", "/srv/logs", "/srv", "/srv" }, service.RequestedPaths);
    }

    [Fact]
    public async Task NavigateUpAsync_MovesToParentDirectory()
    {
        var service = new FakeRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("/srv/api", Array.Empty<RemoteSidebarEntry>()),
            RemoteSidebarListingResult.Success("/srv", Array.Empty<RemoteSidebarEntry>()));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv/api", CancellationToken.None);

        await viewModel.NavigateUpAsync();

        Assert.Equal("/srv", viewModel.CurrentPath);
        Assert.Equal(new[] { "/srv/api", "/srv" }, service.RequestedPaths);
    }

    [Fact]
    public async Task SetJumpToCurrentDirectoryCandidate_ExposesJumpAffordance_WithoutNavigating()
    {
        var service = new FakeRemoteDirectoryBrowserService("/srv", Array.Empty<RemoteSidebarEntry>());
        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        viewModel.SetJumpToCurrentDirectoryCandidate("/srv/api");

        Assert.Equal("/srv/api", viewModel.JumpToCurrentDirectoryPath);
        Assert.Equal("/srv", viewModel.CurrentPath);
        Assert.Equal(new[] { "/srv" }, service.RequestedPaths);
    }

    [Fact]
    public async Task OpenAsync_WhenListingFails_SetsInlineError_AndKeepsSidebarOpen()
    {
        var service = new FakeRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Failure("/srv", "permission denied"));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        Assert.True(viewModel.IsOpen);
        Assert.Equal("/srv", viewModel.CurrentPath);
        Assert.Equal("permission denied", viewModel.ErrorMessage);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task MarkDisconnected_KeepsSidebarOpenButDisablesTransferActions()
    {
        var service = new FakeRemoteDirectoryBrowserService("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        });

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);

        viewModel.MarkDisconnected();

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsDisconnected);
        Assert.False(viewModel.CanInteractWithRemote);
        Assert.False(viewModel.CanDownloadSelected);
        Assert.Null(viewModel.SelectedEntry);
        Assert.Equal("SSH session disconnected.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LaterLoadCompletion_WinsOverEarlierStaleCompletion()
    {
        var service = new ControlledRemoteDirectoryBrowserService();
        service.EnqueueImmediate("/srv", RemoteSidebarListingResult.Success("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        }));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);

        Task navigateIntoTask = viewModel.NavigateIntoSelectedDirectoryAsync();
        await service.WaitForRequestAsync("/srv/logs");

        viewModel.SetJumpToCurrentDirectoryCandidate("/srv/api");
        Task jumpTask = viewModel.JumpToCurrentDirectoryAsync();
        await service.WaitForRequestAsync("/srv/api");

        service.CompletePending("/srv/api", RemoteSidebarListingResult.Success("/srv/api", Array.Empty<RemoteSidebarEntry>()));
        await jumpTask;

        Assert.Equal("/srv/api", viewModel.CurrentPath);
        Assert.True(viewModel.CanNavigateBack);

        service.CompletePending("/srv/logs", RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()));
        await navigateIntoTask;

        Assert.Equal("/srv/api", viewModel.CurrentPath);
        Assert.True(viewModel.CanNavigateBack);
        Assert.Equal(new[] { "/srv", "/srv/logs", "/srv/api" }, service.RequestedPaths);
    }

    private sealed class FakeRemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
    {
        private readonly Queue<RemoteSidebarListingResult> _results;

        public FakeRemoteDirectoryBrowserService(string resolvedPath, IReadOnlyList<RemoteSidebarEntry> entries)
            : this(RemoteSidebarListingResult.Success(resolvedPath, entries))
        {
        }

        public FakeRemoteDirectoryBrowserService(params RemoteSidebarListingResult[] results)
        {
            _results = new Queue<RemoteSidebarListingResult>(results);
        }

        public List<string> RequestedPaths { get; } = new();

        public Task<RemoteSidebarListingResult> ListDirectoryAsync(
            Guid profileId,
            Guid sessionId,
            string remotePath,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(remotePath);

            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No more results configured.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ControlledRemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
    {
        private readonly Dictionary<string, Queue<TaskCompletionSource<RemoteSidebarListingResult>>> _pendingRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<RemoteSidebarListingResult>> _immediateResults = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<object?>> _requestSignals = new(StringComparer.Ordinal);

        public List<string> RequestedPaths { get; } = new();

        public void EnqueueImmediate(string remotePath, RemoteSidebarListingResult result)
        {
            if (!_immediateResults.TryGetValue(remotePath, out Queue<RemoteSidebarListingResult>? results))
            {
                results = new Queue<RemoteSidebarListingResult>();
                _immediateResults[remotePath] = results;
            }

            results.Enqueue(result);
        }

        public async Task WaitForRequestAsync(string remotePath)
        {
            TaskCompletionSource<object?> signal;

            lock (_requestSignals)
            {
                if (!_requestSignals.TryGetValue(remotePath, out signal!))
                {
                    signal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _requestSignals[remotePath] = signal;
                }
            }

            await signal.Task;
        }

        public void CompletePending(string remotePath, RemoteSidebarListingResult result)
        {
            TaskCompletionSource<RemoteSidebarListingResult> pending;

            lock (_pendingRequests)
            {
                pending = _pendingRequests[remotePath].Dequeue();
            }

            pending.SetResult(result);
        }

        public Task<RemoteSidebarListingResult> ListDirectoryAsync(
            Guid profileId,
            Guid sessionId,
            string remotePath,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(remotePath);

            lock (_requestSignals)
            {
                if (_requestSignals.TryGetValue(remotePath, out TaskCompletionSource<object?>? existingSignal))
                {
                    existingSignal.TrySetResult(null);
                }
                else
                {
                    var signal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal.SetResult(null);
                    _requestSignals[remotePath] = signal;
                }
            }

            if (_immediateResults.TryGetValue(remotePath, out Queue<RemoteSidebarListingResult>? immediateResults)
                && immediateResults.Count > 0)
            {
                return Task.FromResult(immediateResults.Dequeue());
            }

            var pending = new TaskCompletionSource<RemoteSidebarListingResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_pendingRequests)
            {
                if (!_pendingRequests.TryGetValue(remotePath, out Queue<TaskCompletionSource<RemoteSidebarListingResult>>? requests))
                {
                    requests = new Queue<TaskCompletionSource<RemoteSidebarListingResult>>();
                    _pendingRequests[remotePath] = requests;
                }

                requests.Enqueue(pending);
            }

            return pending.Task;
        }
    }
}
