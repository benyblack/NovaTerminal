using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using NovaTerminal.Controls;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Core;

public sealed class RemoteFilesSidebarTests
{
    [AvaloniaFact]
    public void DownloadButton_IsDisabled_WhenNothingIsSelected()
    {
        var viewModel = new RemoteFilesSidebarViewModel(new FakeRemoteDirectoryBrowserService());
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        Button button = control.FindControl<Button>("BtnDownloadSelected")!;
        Assert.False(button.IsEnabled);
    }

    [AvaloniaFact]
    public async Task InlineErrorText_IsVisible_WhenErrorMessageIsSet()
    {
        var viewModel = new RemoteFilesSidebarViewModel(
            new FakeRemoteDirectoryBrowserService(
                RemoteSidebarListingResult.Failure("/srv", "permission denied")));
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        TextBlock errorText = control.FindControl<TextBlock>("InlineErrorText")!;
        Assert.True(errorText.IsVisible);
        Assert.Equal("permission denied", errorText.Text);
    }

    [AvaloniaFact]
    public async Task JumpToCurrentDirectoryButton_IsVisible_WhenCandidatePathExists()
    {
        var viewModel = new RemoteFilesSidebarViewModel(
            new FakeRemoteDirectoryBrowserService("/srv", Array.Empty<RemoteSidebarEntry>()));
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        viewModel.SetJumpToCurrentDirectoryCandidate("/srv/api");

        Button button = control.FindControl<Button>("BtnJumpToCwd")!;
        Assert.True(button.IsVisible);
    }

    [AvaloniaFact]
    public async Task ActivatingSelectedDirectory_NavigatesIntoDirectory()
    {
        var viewModel = new RemoteFilesSidebarViewModel(
            new FakeRemoteDirectoryBrowserService(
                RemoteSidebarListingResult.Success("/srv", new[]
                {
                    new RemoteSidebarEntry("logs", "/srv/logs", true)
                }),
                RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>())));
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        ListBox entriesList = control.FindControl<ListBox>("RemoteEntriesList")!;
        entriesList.SelectedIndex = 0;
        entriesList.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = entriesList
        });

        Assert.Equal("/srv/logs", viewModel.CurrentPath);
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

        public Task<RemoteSidebarListingResult> ListDirectoryAsync(
            Guid profileId,
            Guid sessionId,
            string remotePath,
            CancellationToken cancellationToken)
        {
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No more results configured.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }
}
