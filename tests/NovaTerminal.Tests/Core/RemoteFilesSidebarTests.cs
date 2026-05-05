using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NovaTerminal.Controls;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Core;

public sealed class RemoteFilesSidebarTests
{
    [AvaloniaFact]
    public void Sidebar_UsesCompactChromeWidth()
    {
        var control = new RemoteFilesSidebar();

        Border chrome = control.FindControl<Border>("SidebarChrome")!;
        Assert.Equal(288d, chrome.Width);
    }

    [AvaloniaFact]
    public void Sidebar_UsesHostHeader_AndCompactPathRow()
    {
        var control = new RemoteFilesSidebar();

        Assert.NotNull(control.FindControl<TextBlock>("HostTitleText"));
        Assert.NotNull(control.FindControl<TextBlock>("HostSubtitleText"));

        TextBlock currentPathText = control.FindControl<TextBlock>("CurrentPathText")!;
        Assert.Equal(TextTrimming.CharacterEllipsis, currentPathText.TextTrimming);

        Assert.NotNull(control.FindControl<TextBlock>("ItemCountText"));
    }

    [AvaloniaFact]
    public void Footer_UsesCompactDownloadActionLabel_AndPreservesExistingButtonBindings()
    {
        var control = new RemoteFilesSidebar();

        Assert.NotNull(control.FindControl<Button>("BtnUploadFile"));
        Assert.NotNull(control.FindControl<Button>("BtnUploadFolder"));

        Button downloadButton = control.FindControl<Button>("BtnDownloadSelected")!;
        Assert.Equal("Download", downloadButton.Content);
    }

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
    public async Task NavigationButtons_AreDisabled_WhileLoading()
    {
        var service = new ControlledRemoteDirectoryBrowserService();
        var viewModel = new RemoteFilesSidebarViewModel(
            service);
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        Button navigateUpButton = control.FindControl<Button>("BtnNavigateUp")!;
        Button refreshButton = control.FindControl<Button>("BtnRefresh")!;
        Button jumpToCwdButton = control.FindControl<Button>("BtnJumpToCwd")!;

        Task openTask = viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);
        await service.WaitForRequestAsync("/srv");
        viewModel.SetJumpToCurrentDirectoryCandidate("/srv/api");

        Assert.True(viewModel.IsLoading);
        Assert.False(navigateUpButton.IsEffectivelyEnabled);
        Assert.False(refreshButton.IsEffectivelyEnabled);
        Assert.False(jumpToCwdButton.IsEffectivelyEnabled);

        service.CompletePending("/srv", RemoteSidebarListingResult.Success("/srv", Array.Empty<RemoteSidebarEntry>()));
        await openTask;
    }

    [AvaloniaFact]
    public async Task BackButton_IsDisabled_WhileLoadingNavigation()
    {
        var service = new ControlledRemoteDirectoryBrowserService();
        service.EnqueueImmediate("/srv", RemoteSidebarListingResult.Success("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        }));
        service.EnqueueImmediate("/srv/logs", RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        ListBox entriesList = control.FindControl<ListBox>("RemoteEntriesList")!;
        Button navigateBackButton = control.FindControl<Button>("BtnNavigateBack")!;
        entriesList.SelectedIndex = 0;
        entriesList.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = entriesList
        });
        await WaitForConditionAsync(() => viewModel.CurrentPath == "/srv/logs");

        Task navigateBackTask = viewModel.NavigateBackAsync();
        await service.WaitForRequestAsync("/srv");

        Assert.True(viewModel.CanNavigateBack);
        Assert.True(viewModel.IsLoading);
        Assert.False(navigateBackButton.IsEffectivelyEnabled);

        service.CompletePending("/srv", RemoteSidebarListingResult.Success("/srv", Array.Empty<RemoteSidebarEntry>()));
        await navigateBackTask;
    }

    [AvaloniaFact]
    public async Task BackButton_IsDisabled_AfterDisconnect()
    {
        var service = new ControlledRemoteDirectoryBrowserService();
        service.EnqueueImmediate("/srv", RemoteSidebarListingResult.Success("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        }));
        service.EnqueueImmediate("/srv/logs", RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        ListBox entriesList = control.FindControl<ListBox>("RemoteEntriesList")!;
        Button navigateBackButton = control.FindControl<Button>("BtnNavigateBack")!;
        entriesList.SelectedIndex = 0;
        entriesList.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = entriesList
        });

        await WaitForConditionAsync(() => viewModel.CurrentPath == "/srv/logs");
        Assert.True(viewModel.CanNavigateBack);
        Assert.True(navigateBackButton.IsEffectivelyEnabled);

        viewModel.MarkDisconnected();

        Assert.True(viewModel.CanNavigateBack);
        Assert.True(viewModel.IsDisconnected);
        Assert.False(navigateBackButton.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task EnterActivation_HandlesKeyEvent_BeforeAsyncNavigationCompletes()
    {
        var service = new ControlledRemoteDirectoryBrowserService();
        service.EnqueueImmediate("/srv", RemoteSidebarListingResult.Success("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        }));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        bool bubbledToParent = false;
        control.AddHandler(InputElement.KeyDownEvent, (_, _) => bubbledToParent = true, RoutingStrategies.Bubble);

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        ListBox entriesList = control.FindControl<ListBox>("RemoteEntriesList")!;
        entriesList.SelectedIndex = 0;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = entriesList
        };

        entriesList.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.False(bubbledToParent);
        Assert.True(viewModel.IsLoading);

        await service.WaitForRequestAsync("/srv/logs");
        service.CompletePending("/srv/logs", RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()));
        await WaitForConditionAsync(() => viewModel.CurrentPath == "/srv/logs");

        Assert.Equal("/srv/logs", viewModel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task DoubleTapActivation_NavigatesIntoDirectory()
    {
        var service = new ControlledRemoteDirectoryBrowserService();
        service.EnqueueImmediate("/srv", RemoteSidebarListingResult.Success("/srv", new[]
        {
            new RemoteSidebarEntry("logs", "/srv/logs", true)
        }));

        var viewModel = new RemoteFilesSidebarViewModel(service);
        var control = new RemoteFilesSidebar
        {
            DataContext = viewModel
        };

        await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

        ListBox entriesList = control.FindControl<ListBox>("RemoteEntriesList")!;
        entriesList.SelectedIndex = 0;
        entriesList.RaiseEvent(new RoutedEventArgs(InputElement.DoubleTappedEvent));

        await service.WaitForRequestAsync("/srv/logs");
        service.CompletePending("/srv/logs", RemoteSidebarListingResult.Success("/srv/logs", Array.Empty<RemoteSidebarEntry>()));
        await WaitForConditionAsync(() => viewModel.CurrentPath == "/srv/logs");

        Assert.Equal("/srv/logs", viewModel.CurrentPath);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(predicate(), "Timed out waiting for condition.");
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

    private sealed class ControlledRemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
    {
        private readonly Dictionary<string, Queue<TaskCompletionSource<RemoteSidebarListingResult>>> _pendingRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<RemoteSidebarListingResult>> _immediateResults = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<object?>> _requestSignals = new(StringComparer.Ordinal);

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
