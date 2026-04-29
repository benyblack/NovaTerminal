using NovaTerminal.Core;
using NovaTerminal.Models;

namespace NovaTerminal.Tests.Core;

public sealed class TransferDialogRequestTests
{
    [Fact]
    public void ForAction_UsesRequestedModeAndDefaultRemotePath()
    {
        Guid profileId = Guid.Parse("aa16f38f-694c-4bbd-8c4e-eae16da5dd88");
        Guid sessionId = Guid.Parse("79fbd43b-75e0-43cb-aebf-9f9919ddd528");
        TransferDialogRequest request = TransferDialogRequest.ForAction(
            direction: TransferDirection.Download,
            kind: TransferKind.File,
            defaultRemotePath: "~/downloads",
            profileId: profileId,
            sessionId: sessionId);

        Assert.Equal(TransferDirection.Download, request.Direction);
        Assert.Equal(TransferKind.File, request.Kind);
        Assert.Equal("~/downloads", request.RemotePath);
        Assert.Equal(profileId, request.ProfileId);
        Assert.Equal(sessionId, request.SessionId);
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
