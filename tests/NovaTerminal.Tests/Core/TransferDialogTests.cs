using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

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

    [AvaloniaFact]
    public void TransferDialog_PassesProfileAndSessionContext_ToRemotePathInput()
    {
        Guid profileId = Guid.Parse("6b9ddc71-9bad-4121-9ecf-50bc3da67495");
        Guid sessionId = Guid.Parse("5be0adf2-489f-4a77-8246-a183b6f7685d");
        var autocompleteService = new FakeRemotePathAutocompleteService();
        var dialog = new TransferDialog(
            new TransferDialogRequest
            {
                Direction = TransferDirection.Download,
                Kind = TransferKind.File,
                RemotePath = "~/downloads",
                ProfileId = profileId,
                SessionId = sessionId
            },
            autocompleteService);

        RemotePathTextBox? remotePathInput = dialog.FindControl<RemotePathTextBox>("RemotePathInput");

        Assert.NotNull(remotePathInput);
        Assert.Equal(profileId, remotePathInput!.ProfileId);
        Assert.Equal(sessionId, remotePathInput.SessionId);
        Assert.Same(autocompleteService, remotePathInput.AutocompleteService);
    }

    private sealed class FakeRemotePathAutocompleteService : IRemotePathAutocompleteService
    {
        public Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
            Guid profileId,
            Guid sessionId,
            string input,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<RemotePathSuggestion>>([]);
        }
    }
}
