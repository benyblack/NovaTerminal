using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

        Button cancelButton = this.FindControl<Button>("BtnTransferCancel")
            ?? throw new InvalidOperationException("BtnTransferCancel was not found.");

        Title = BuildTitle(request.Direction, request.Kind);
        _remotePathBox.Text = request.RemotePath;
        _remotePathBox.PropertyChanged += OnPathBoxPropertyChanged;
        _localPathBox.PropertyChanged += OnPathBoxPropertyChanged;
        _confirmButton.Click += OnConfirmClick;
        cancelButton.Click += OnCancelClick;
        Opened += OnOpened;

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
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
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
}
