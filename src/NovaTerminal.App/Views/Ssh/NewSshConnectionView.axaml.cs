using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Views.Ssh;

public partial class NewSshConnectionView : Window
{
    public NewSshConnectionView()
    {
        InitializeComponent();
        ConfigureAuthModeCombo();
        ConfigureForwardKindCombo();
    }

    public NewSshConnectionView(NewSshConnectionViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private NewSshConnectionViewModel? ViewModel => DataContext as NewSshConnectionViewModel;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ConfigureAuthModeCombo()
    {
        var combo = this.FindControl<ComboBox>("AuthModeCombo");
        if (combo != null)
        {
            combo.ItemsSource = Enum.GetValues<NewSshAuthMode>();
        }
    }

    private void ConfigureForwardKindCombo()
    {
        var combo = this.FindControl<ComboBox>("ForwardKindCombo");
        if (combo != null)
        {
            combo.ItemsSource = Enum.GetValues<PortForwardKind>();
            combo.SelectedItem = PortForwardKind.Local;
        }

        var sourcePortInput = this.FindControl<NumericUpDown>("ForwardSourcePortInput");
        if (sourcePortInput != null)
        {
            sourcePortInput.Value = 8080;
        }

        var destPortInput = this.FindControl<NumericUpDown>("ForwardDestPortInput");
        if (destPortInput != null)
        {
            destPortInput.Value = 80;
        }
    }

    private async void OnBrowseIdentityFileClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select SSH Identity File",
                AllowMultiple = false
            });

        if (files.Count > 0)
        {
            ViewModel.IdentityFilePath = files[0].Path.LocalPath;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        CompleteSave(connectAfterSave: false);
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        CompleteSave(connectAfterSave: true);
    }

    private void OnAddJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var hostInput = this.FindControl<TextBox>("JumpHostInput");
        var userInput = this.FindControl<TextBox>("JumpUserInput");
        var portInput = this.FindControl<NumericUpDown>("JumpPortInput");
        if (hostInput == null || portInput == null)
        {
            return;
        }

        string host = hostInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            ViewModel.ValidationError = "Jump host requires a host value.";
            return;
        }

        int port = portInput.Value.HasValue ? (int)portInput.Value.Value : 22;
        ViewModel.JumpHops.Add(new SshJumpHop
        {
            Host = host,
            User = userInput?.Text?.Trim() ?? string.Empty,
            Port = port > 0 ? port : 22
        });

        ViewModel.ValidationError = string.Empty;
        hostInput.Text = string.Empty;
        if (userInput != null)
        {
            userInput.Text = string.Empty;
        }
        portInput.Value = 22;
    }

    private void OnRemoveJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("JumpHostsList");
        if (list?.SelectedItem is not SshJumpHop selected)
        {
            return;
        }

        ViewModel.JumpHops.Remove(selected);
    }

    private void OnMoveJumpUpClick(object? sender, RoutedEventArgs e)
    {
        MoveSelectedJumpHop(-1);
    }

    private void OnMoveJumpDownClick(object? sender, RoutedEventArgs e)
    {
        MoveSelectedJumpHop(1);
    }

    private void MoveSelectedJumpHop(int offset)
    {
        if (ViewModel == null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("JumpHostsList");
        if (list?.SelectedItem is not SshJumpHop selected)
        {
            return;
        }

        int currentIndex = ViewModel.JumpHops.IndexOf(selected);
        if (currentIndex < 0)
        {
            return;
        }

        int newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= ViewModel.JumpHops.Count)
        {
            return;
        }

        ViewModel.JumpHops.Move(currentIndex, newIndex);
        list.SelectedItem = selected;
    }

    private void OnAddForwardClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var kindCombo = this.FindControl<ComboBox>("ForwardKindCombo");
        var bindInput = this.FindControl<TextBox>("ForwardBindInput");
        var sourcePortInput = this.FindControl<NumericUpDown>("ForwardSourcePortInput");
        var destHostInput = this.FindControl<TextBox>("ForwardDestHostInput");
        var destPortInput = this.FindControl<NumericUpDown>("ForwardDestPortInput");

        if (kindCombo?.SelectedItem is not PortForwardKind kind || sourcePortInput == null || !sourcePortInput.Value.HasValue)
        {
            return;
        }

        int sourcePort = (int)sourcePortInput.Value.Value;
        int destinationPort = destPortInput?.Value.HasValue == true ? (int)destPortInput.Value.Value : 0;
        string destinationHost = destHostInput?.Text?.Trim() ?? string.Empty;

        if (sourcePort <= 0)
        {
            ViewModel.ValidationError = "Forward source port must be between 1 and 65535.";
            return;
        }

        if (kind != PortForwardKind.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(destinationHost))
            {
                ViewModel.ValidationError = "Forward destination host is required for local/remote forwarding.";
                return;
            }

            if (destinationPort <= 0)
            {
                ViewModel.ValidationError = "Forward destination port must be between 1 and 65535.";
                return;
            }
        }

        ViewModel.Forwards.Add(new PortForward
        {
            Kind = kind,
            BindAddress = bindInput?.Text?.Trim() ?? string.Empty,
            SourcePort = sourcePort,
            DestinationHost = destinationHost,
            DestinationPort = destinationPort
        });

        ViewModel.ValidationError = string.Empty;
        if (bindInput != null)
        {
            bindInput.Text = string.Empty;
        }
        if (destHostInput != null)
        {
            destHostInput.Text = string.Empty;
        }
        if (destPortInput != null)
        {
            destPortInput.Value = 80;
        }
    }

    private void OnRemoveForwardClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("ForwardsList");
        if (list?.SelectedItem is not PortForward selected)
        {
            return;
        }

        ViewModel.Forwards.Remove(selected);
    }

    private void CompleteSave(bool connectAfterSave)
    {
        if (ViewModel == null)
        {
            Close(false);
            return;
        }

        ViewModel.ConnectAfterSave = connectAfterSave;
        if (!ViewModel.Validate())
        {
            return;
        }

        Close(true);
    }
}
