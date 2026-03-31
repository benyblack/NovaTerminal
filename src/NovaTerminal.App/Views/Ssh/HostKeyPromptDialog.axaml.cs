using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Views.Ssh;

public partial class HostKeyPromptDialog : Window
{
    public HostKeyPromptDialog()
    {
        InitializeComponent();
    }

    public HostKeyPromptDialog(HostKeyPromptViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Close(SshInteractionResponse.AcceptHostKey());
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(SshInteractionResponse.Cancel());
    }
}
