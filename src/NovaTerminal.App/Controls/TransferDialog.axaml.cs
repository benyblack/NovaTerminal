using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NovaTerminal.Core;
using NovaTerminal.Models;

namespace NovaTerminal.Controls;

public partial class TransferDialog : Window
{
    private readonly TransferDialogRequest _request;
    private readonly TextBox _remotePathBox;
    private readonly TextBox _localPathBox;
    private readonly TextBlock _validationMessage;
    private readonly Button _confirmButton;

    public TransferDialog()
        : this(new TransferDialogRequest
        {
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            RemotePath = string.Empty
        })
    {
    }

    public TransferDialog(TransferDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _request = request;
        InitializeComponent();

        _remotePathBox = this.FindControl<TextBox>("RemotePathBox")
            ?? throw new InvalidOperationException("RemotePathBox was not found.");
        _localPathBox = this.FindControl<TextBox>("LocalPathBox")
            ?? throw new InvalidOperationException("LocalPathBox was not found.");
        _validationMessage = this.FindControl<TextBlock>("ValidationMessage")
            ?? throw new InvalidOperationException("ValidationMessage was not found.");
        _confirmButton = this.FindControl<Button>("BtnTransferConfirm")
            ?? throw new InvalidOperationException("BtnTransferConfirm was not found.");
        Button browseButton = this.FindControl<Button>("BtnBrowseLocal")
            ?? throw new InvalidOperationException("BtnBrowseLocal was not found.");

        Button cancelButton = this.FindControl<Button>("BtnTransferCancel")
            ?? throw new InvalidOperationException("BtnTransferCancel was not found.");

        Title = BuildTitle(request.Direction, request.Kind);
        _remotePathBox.Text = request.RemotePath;
        _remotePathBox.PropertyChanged += OnPathBoxPropertyChanged;
        _localPathBox.PropertyChanged += OnPathBoxPropertyChanged;
        _confirmButton.Click += OnConfirmClick;
        cancelButton.Click += OnCancelClick;
        browseButton.Click += OnBrowseLocalClick;
        Opened += OnOpened;
        KeyDown += OnDialogKeyDown;

        RefreshValidation();
    }

    public TransferDialogResult? Result { get; private set; }

    private void OnOpened(object? sender, EventArgs e)
    {
        _remotePathBox.Focus();
    }

    private void OnPathBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            RefreshValidation();
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        RefreshValidation();
        if (!_confirmButton.IsEnabled)
        {
            return;
        }

        Result = TransferDialogResult.CreateConfirmed(
            _localPathBox.Text?.Trim() ?? string.Empty,
            _remotePathBox.Text?.Trim() ?? string.Empty);
        Close(Result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(null);
    }

    private async void OnBrowseLocalClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return;
        }

        if (_request.Direction == TransferDirection.Upload)
        {
            if (_request.Kind == TransferKind.File)
            {
                IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select File to Upload",
                    AllowMultiple = false
                });

                if (files.Count > 0)
                {
                    _localPathBox.Text = files[0].Path.LocalPath;
                }
            }
            else
            {
                IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Folder to Upload",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    _localPathBox.Text = folders[0].Path.LocalPath;
                }
            }

            return;
        }

        if (_request.Kind == TransferKind.File)
        {
            IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Select Local Destination",
                SuggestedFileName = ResolveSuggestedFileName()
            });

            if (file != null)
            {
                _localPathBox.Text = file.Path.LocalPath;
            }

            return;
        }

        IReadOnlyList<IStorageFolder> destinationFolders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Local Destination Folder",
            AllowMultiple = false
        });

        if (destinationFolders.Count > 0)
        {
            _localPathBox.Text = destinationFolders[0].Path.LocalPath;
        }
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCancelClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _confirmButton.IsEnabled)
        {
            OnConfirmClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void RefreshValidation()
    {
        bool hasRemote = !string.IsNullOrWhiteSpace(_remotePathBox.Text);
        bool hasLocal = !string.IsNullOrWhiteSpace(_localPathBox.Text);
        bool valid = hasRemote && hasLocal;

        _confirmButton.IsEnabled = valid;
        _validationMessage.Text = valid
            ? string.Empty
            : "Local and remote paths are required.";
    }

    private static string BuildTitle(TransferDirection direction, TransferKind kind)
    {
        string action = direction == TransferDirection.Upload ? "Upload" : "Download";
        string item = kind == TransferKind.Folder ? "Folder" : "File";
        return $"{action} {item}";
    }

    private string ResolveSuggestedFileName()
    {
        string remotePath = _remotePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return "download";
        }

        string trimmed = remotePath.TrimEnd('/', '\\');
        string fileName = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
    }
}
