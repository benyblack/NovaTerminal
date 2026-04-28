namespace NovaTerminal.Models;

public sealed class TransferDialogResult
{
    public bool IsConfirmed { get; init; }
    public string LocalPath { get; init; } = string.Empty;
    public string RemotePath { get; init; } = string.Empty;

    public static TransferDialogResult CreateConfirmed(string localPath, string remotePath)
    {
        return new TransferDialogResult
        {
            IsConfirmed = true,
            LocalPath = localPath,
            RemotePath = remotePath
        };
    }
}
