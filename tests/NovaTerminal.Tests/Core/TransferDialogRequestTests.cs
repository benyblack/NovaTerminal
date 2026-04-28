using NovaTerminal.Core;
using NovaTerminal.Models;

namespace NovaTerminal.Tests.Core;

public sealed class TransferDialogRequestTests
{
    [Fact]
    public void ForAction_UsesRequestedModeAndDefaultRemotePath()
    {
        TransferDialogRequest request = TransferDialogRequest.ForAction(
            direction: TransferDirection.Download,
            kind: TransferKind.File,
            defaultRemotePath: "~/downloads");

        Assert.Equal(TransferDirection.Download, request.Direction);
        Assert.Equal(TransferKind.File, request.Kind);
        Assert.Equal("~/downloads", request.RemotePath);
    }

    [Fact]
    public void Result_CreateConfirmed_PreservesSelectedPaths()
    {
        TransferDialogResult result = TransferDialogResult.CreateConfirmed(
            localPath: @"D:\downloads\a.mkv",
            remotePath: "/mnt/box/movies/a.mkv");

        Assert.True(result.IsConfirmed);
        Assert.Equal(@"D:\downloads\a.mkv", result.LocalPath);
        Assert.Equal("/mnt/box/movies/a.mkv", result.RemotePath);
    }
}
