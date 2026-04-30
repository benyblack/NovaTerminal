using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.ViewModels.Ssh;

public sealed class RemoteFilesSidebarViewModel : INotifyPropertyChanged
{
    private readonly IRemoteDirectoryBrowserService _directoryBrowserService;
    private readonly Stack<string> _backStack = new();
    private readonly HashSet<long> _pendingLoadVersions = new();
    private Guid _profileId;
    private Guid _sessionId;
    private bool _isOpen;
    private bool _isLoading;
    private bool _isDisconnected;
    private string _currentPath = string.Empty;
    private string? _jumpToCurrentDirectoryPath;
    private RemoteFilesSidebarEntryViewModel? _selectedEntry;
    private string? _errorMessage;
    private long _latestRequestedLoadVersion;
    private long _nextLoadVersion;

    public RemoteFilesSidebarViewModel(IRemoteDirectoryBrowserService directoryBrowserService)
    {
        _directoryBrowserService = directoryBrowserService ?? throw new ArgumentNullException(nameof(directoryBrowserService));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetField(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(CanInteractWithRemote));
                OnPropertyChanged(nameof(CanDownloadSelected));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanInteractWithRemote));
            }
        }
    }

    public bool IsDisconnected
    {
        get => _isDisconnected;
        private set
        {
            if (SetField(ref _isDisconnected, value))
            {
                OnPropertyChanged(nameof(CanInteractWithRemote));
                OnPropertyChanged(nameof(CanDownloadSelected));
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetField(ref _currentPath, value);
    }

    public string? JumpToCurrentDirectoryPath
    {
        get => _jumpToCurrentDirectoryPath;
        private set => SetField(ref _jumpToCurrentDirectoryPath, value);
    }

    public ObservableCollection<RemoteFilesSidebarEntryViewModel> Entries { get; } = new();

    public RemoteFilesSidebarEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(CanDownloadSelected));
            }
        }
    }

    public bool CanInteractWithRemote => IsOpen && !IsDisconnected && !IsLoading;

    public bool CanDownloadSelected => SelectedEntry is not null && IsOpen && !IsDisconnected;

    public bool CanNavigateBack => _backStack.Count > 0;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public async Task OpenAsync(
        Guid profileId,
        Guid sessionId,
        string initialPath,
        CancellationToken cancellationToken)
    {
        _profileId = profileId;
        _sessionId = sessionId;
        _backStack.Clear();
        InvalidatePendingLoads();
        OnPropertyChanged(nameof(CanNavigateBack));
        IsDisconnected = false;
        IsOpen = true;
        JumpToCurrentDirectoryPath = null;

        await LoadDirectoryAsync(initialPath, BackStackMutation.None, backStackPath: null, cancellationToken);
    }

    public void Close()
    {
        _backStack.Clear();
        InvalidatePendingLoads();
        OnPropertyChanged(nameof(CanNavigateBack));
        Entries.Clear();
        SelectedEntry = null;
        ErrorMessage = null;
        JumpToCurrentDirectoryPath = null;
        CurrentPath = string.Empty;
        IsLoading = false;
        IsDisconnected = false;
        IsOpen = false;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen || string.IsNullOrWhiteSpace(CurrentPath))
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(CurrentPath, BackStackMutation.None, backStackPath: null, cancellationToken);
    }

    public Task NavigateIntoSelectedDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedEntry is null || !SelectedEntry.IsDirectory)
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(
            SelectedEntry.FullPath,
            BackStackMutation.Push,
            backStackPath: CurrentPath,
            cancellationToken);
    }

    public Task NavigateUpAsync(CancellationToken cancellationToken = default)
    {
        string? parentPath = GetParentPath(CurrentPath);
        if (parentPath is null)
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(
            parentPath,
            BackStackMutation.Push,
            backStackPath: CurrentPath,
            cancellationToken);
    }

    public Task NavigateBackAsync(CancellationToken cancellationToken = default)
    {
        if (_backStack.Count == 0)
        {
            return Task.CompletedTask;
        }

        string previousPath = _backStack.Peek();
        return LoadDirectoryAsync(
            previousPath,
            BackStackMutation.PopOnSuccess,
            backStackPath: previousPath,
            cancellationToken);
    }

    public Task JumpToCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(JumpToCurrentDirectoryPath))
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(
            JumpToCurrentDirectoryPath,
            BackStackMutation.Push,
            backStackPath: CurrentPath,
            cancellationToken);
    }

    public void SetJumpToCurrentDirectoryCandidate(string? remotePath)
    {
        JumpToCurrentDirectoryPath = string.IsNullOrWhiteSpace(remotePath)
            ? null
            : remotePath.Trim();
    }

    public void MarkDisconnected()
    {
        InvalidatePendingLoads();
        JumpToCurrentDirectoryPath = null;
        SelectedEntry = null;
        ErrorMessage = "SSH session disconnected.";
        IsDisconnected = true;
        IsLoading = false;
    }

    private async Task LoadDirectoryAsync(
        string remotePath,
        BackStackMutation backStackMutation,
        string? backStackPath,
        CancellationToken cancellationToken)
    {
        long loadVersion = BeginLoad();

        try
        {
            RemoteSidebarListingResult result = await _directoryBrowserService
                .ListDirectoryAsync(_profileId, _sessionId, remotePath, cancellationToken);

            if (loadVersion != _latestRequestedLoadVersion || !IsOpen)
            {
                return;
            }

            ApplyBackStackMutation(backStackMutation, backStackPath, result.IsSuccess);
            ApplyListingResult(result);
        }
        finally
        {
            EndLoad(loadVersion);
        }
    }

    private long BeginLoad()
    {
        long loadVersion = ++_nextLoadVersion;
        _latestRequestedLoadVersion = loadVersion;
        _pendingLoadVersions.Add(loadVersion);
        IsLoading = true;
        IsDisconnected = false;
        return loadVersion;
    }

    private void EndLoad(long loadVersion)
    {
        _pendingLoadVersions.Remove(loadVersion);
        IsLoading = _pendingLoadVersions.Count > 0;
    }

    private void ApplyBackStackMutation(
        BackStackMutation mutation,
        string? backStackPath,
        bool isSuccess)
    {
        if (!isSuccess || string.IsNullOrWhiteSpace(backStackPath))
        {
            return;
        }

        switch (mutation)
        {
            case BackStackMutation.Push:
                _backStack.Push(backStackPath);
                OnPropertyChanged(nameof(CanNavigateBack));
                break;

            case BackStackMutation.PopOnSuccess:
                if (_backStack.Count > 0 && string.Equals(_backStack.Peek(), backStackPath, StringComparison.Ordinal))
                {
                    _backStack.Pop();
                    OnPropertyChanged(nameof(CanNavigateBack));
                }

                break;
        }
    }

    private void ApplyListingResult(RemoteSidebarListingResult result)
    {
        CurrentPath = result.ResolvedPath;
        ErrorMessage = result.ErrorMessage;
        Entries.Clear();

        foreach (RemoteSidebarEntry entry in result.Entries)
        {
            Entries.Add(new RemoteFilesSidebarEntryViewModel(entry));
        }

        SelectedEntry = null;
    }

    private static string? GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalizedPath = path.TrimEnd('/');
        if (normalizedPath.Length == 0 || normalizedPath == "/" || normalizedPath == "~")
        {
            return null;
        }

        int separatorIndex = normalizedPath.LastIndexOf('/');
        if (separatorIndex < 0)
        {
            return null;
        }

        if (separatorIndex == 0)
        {
            return "/";
        }

        return normalizedPath[..separatorIndex];
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void InvalidatePendingLoads()
    {
        _latestRequestedLoadVersion = ++_nextLoadVersion;
        _pendingLoadVersions.Clear();
        IsLoading = false;
    }

    private enum BackStackMutation
    {
        None = 0,
        Push = 1,
        PopOnSuccess = 2
    }
}
