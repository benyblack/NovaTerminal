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
    public async Task NavigateIntoSelectedDirectoryAsync_LoadsChildPath_AndEnablesNavigateBack()
    {
        var service = new FakeRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("/srv", new[]
            {
                new RemoteSidebarEntry("logs", "/srv/logs", true)
            }),
            RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);

        await viewModel.NavigateIntoSelectedDirectoryAsync();

        Assert.Equal("/srv/logs", viewModel.CurrentPath);
        Assert.True(viewModel.CanNavigateBack);
        Assert.Equal(new[] { "/srv", "/srv/logs" }, service.RequestedPaths);
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
}
