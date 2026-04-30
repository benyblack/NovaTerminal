using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Controls;

public partial class RemoteFilesSidebar : UserControl
{
    public RemoteFilesSidebar()
    {
        InitializeComponent();
    }

    private RemoteFilesSidebarViewModel? ViewModel => DataContext as RemoteFilesSidebarViewModel;

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
}
