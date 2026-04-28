using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using NovaTerminal.Models;

namespace NovaTerminal.Tests.Core;

public sealed class TransferDialogTests
{
    [AvaloniaFact]
    public void TransferDialog_DisablesConfirm_WhenRequiredPathsAreBlank()
    {
        var dialog = new TransferDialog(new TransferDialogRequest
        {
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            RemotePath = string.Empty
        });

        Button? confirm = dialog.FindControl<Button>("BtnTransferConfirm");
        TextBlock? validation = dialog.FindControl<TextBlock>("ValidationMessage");

        Assert.NotNull(confirm);
        Assert.NotNull(validation);
        Assert.False(confirm!.IsEnabled);
        Assert.Contains("required", validation!.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void TransferDialog_EnablesConfirm_WhenLocalAndRemotePathsArePresent()
    {
        var dialog = new TransferDialog(new TransferDialogRequest
        {
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            RemotePath = "/mnt/box/movies/a.mkv"
        });

        TextBox? localPath = dialog.FindControl<TextBox>("LocalPathBox");
        Button? confirm = dialog.FindControl<Button>("BtnTransferConfirm");

        Assert.NotNull(localPath);
        Assert.NotNull(confirm);

        localPath!.Text = @"D:\downloads\a.mkv";

        Assert.True(confirm!.IsEnabled);
    }
}
