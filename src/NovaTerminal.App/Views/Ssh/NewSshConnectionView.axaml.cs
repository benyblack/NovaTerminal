using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Views.Ssh;

public partial class NewSshConnectionView : Window
{
    public NewSshConnectionView()
    {
        InitializeComponent();
        ConfigureAuthModeCombo();
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
