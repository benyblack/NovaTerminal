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
        Assert.False(request.PreferLocalPathOnOpen);
    }

    [Fact]
    public void ForSidebarAction_UsesProvidedRemotePath_AndPrefersLocalPathFocus()
    {
        Guid profileId = Guid.Parse("9a3c66f6-0aab-4b6d-9c0f-9d8d8568a0ee");
        Guid sessionId = Guid.Parse("65e3dbca-7df7-4698-b6ee-9e12d2f3c7e5");
        TransferDialogRequest request = TransferDialogRequest.ForSidebarAction(
            direction: TransferDirection.Download,
            kind: TransferKind.Folder,
            remotePath: "/srv/logs",
            profileId: profileId,
            sessionId: sessionId);

        Assert.Equal(TransferDirection.Download, request.Direction);
        Assert.Equal(TransferKind.Folder, request.Kind);
        Assert.Equal("/srv/logs", request.RemotePath);
        Assert.Equal(profileId, request.ProfileId);
        Assert.Equal(sessionId, request.SessionId);
        Assert.True(request.PreferLocalPathOnOpen);
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
