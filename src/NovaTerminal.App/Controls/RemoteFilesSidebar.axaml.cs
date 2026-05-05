using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Controls;

public partial class RemoteFilesSidebar : UserControl
{
    public static readonly StyledProperty<string> HostTitleProperty =
        AvaloniaProperty.Register<RemoteFilesSidebar, string>(
            nameof(HostTitle),
            "Remote Files");

    public static readonly StyledProperty<string> HostSubtitleProperty =
        AvaloniaProperty.Register<RemoteFilesSidebar, string>(
            nameof(HostSubtitle),
            "Native SFTP");

    public static readonly StyledProperty<string> ItemCountLabelProperty =
        AvaloniaProperty.Register<RemoteFilesSidebar, string>(
            nameof(ItemCountLabel),
            "0 items");

    private RemoteFilesSidebarViewModel? _observedViewModel;

    public RemoteFilesSidebar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        UpdateObservedViewModel(ViewModel);
    }

    public string HostTitle
    {
        get => GetValue(HostTitleProperty);
        set => SetValue(HostTitleProperty, value);
    }

    public string HostSubtitle
    {
        get => GetValue(HostSubtitleProperty);
        set => SetValue(HostSubtitleProperty, value);
    }

    public string ItemCountLabel
    {
        get => GetValue(ItemCountLabelProperty);
        set => SetValue(ItemCountLabelProperty, value);
    }

    private RemoteFilesSidebarViewModel? ViewModel => DataContext as RemoteFilesSidebarViewModel;

    public void SetHostIdentity(string? title, string? subtitle)
    {
        HostTitle = string.IsNullOrWhiteSpace(title)
            ? "Remote Files"
            : title.Trim();
        HostSubtitle = string.IsNullOrWhiteSpace(subtitle)
            ? "Native SFTP"
            : subtitle.Trim();
    }

    private async void NavigateBack_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.NavigateBackAsync();
    }

    private async void NavigateUp_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.NavigateUpAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RefreshAsync();
    }

    private void CloseSidebar_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Close();
    }

    private async void JumpToCwd_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.JumpToCurrentDirectoryAsync();
    }

    private void RemoteEntriesList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        StartDirectoryNavigation();
    }

    private void RemoteEntriesList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        StartDirectoryNavigation();
    }

    private Task NavigateIntoSelectedDirectoryAsync()
    {
        return ViewModel?.NavigateIntoSelectedDirectoryAsync() ?? Task.CompletedTask;
    }

    private void StartDirectoryNavigation()
    {
        _ = NavigateIntoSelectedDirectoryAsync();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UpdateObservedViewModel(ViewModel);
    }

    private void UpdateObservedViewModel(RemoteFilesSidebarViewModel? viewModel)
    {
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        }

        _observedViewModel = viewModel;

        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _observedViewModel.Entries.CollectionChanged += OnEntriesCollectionChanged;
        }

        UpdateItemCountLabel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteFilesSidebarViewModel.IsOpen) ||
            e.PropertyName == nameof(RemoteFilesSidebarViewModel.IsLoading))
        {
            UpdateItemCountLabel();
        }
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateItemCountLabel();
    }

    private void UpdateItemCountLabel()
    {
        int itemCount = _observedViewModel?.Entries.Count ?? 0;
        ItemCountLabel = itemCount == 1
            ? "1 item"
            : $"{itemCount} items";
    }
}
