using Avalonia.Headless.XUnit;
using NovaTerminal.Core;
using NovaTerminal.Models;

namespace NovaTerminal.Tests.Core;

public sealed class MainWindowTransferFlowTests
{
    [AvaloniaFact]
    public async Task InitiateTransfer_UsesDialogResultAndQueuesJob()
    {
        var window = new TestMainWindow
        {
            NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
                localPath: @"D:\downloads\a.mkv",
                remotePath: "/mnt/box/movies/a.mkv")
        };

        await window.InitiateSftpTransferForTest(
            new TerminalProfile
            {
                Id = Guid.Parse("4f13d6d8-ea72-4d88-9430-fc5d5f2490c7"),
                Name = "server3",
                Type = ConnectionType.SSH,
                SshBackendKind = NovaTerminal.Core.Ssh.Models.SshBackendKind.Native,
                DefaultRemoteDir = "~/downloads"
            },
            Guid.Parse("35ef4c9d-8cc3-4a4f-a2f1-ef7e5215ad7d"),
            TransferDirection.Download,
            TransferKind.File);

        Assert.NotNull(window.QueuedJob);
        TransferJob job = window.QueuedJob!;
        Assert.Equal("/mnt/box/movies/a.mkv", job.RemotePath);
        Assert.Equal(@"D:\downloads\a.mkv", job.LocalPath);
        Assert.Equal(TransferDirection.Download, job.Direction);
        Assert.Equal(TransferKind.File, job.Kind);
    }

    [AvaloniaFact]
    public async Task SidebarDownload_UsesSelectedRemoteEntry_AsTransferRemotePath()
    {
        var window = new TestMainWindow
        {
            NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
                localPath: @"D:\downloads\logs",
                remotePath: "/srv/logs")
        };

        await window.StartSidebarDownloadForTest(
            profile: CreateNativeProfile(),
            sessionId: Guid.Parse("2af2fbad-a4d3-4aeb-bf1d-e8884c0ad6f0"),
            selectedRemotePath: "/srv/logs",
            kind: TransferKind.Folder);

        Assert.NotNull(window.LastTransferDialogRequest);
        Assert.Equal("/srv/logs", window.LastTransferDialogRequest!.RemotePath);
        Assert.True(window.LastTransferDialogRequest.PreferLocalPathOnOpen);
        Assert.NotNull(window.QueuedJob);
        Assert.Equal("/srv/logs", window.QueuedJob!.RemotePath);
        Assert.Equal(TransferKind.Folder, window.QueuedJob.Kind);
    }

    [AvaloniaFact]
    public async Task SidebarUpload_UsesCurrentRemoteDirectory_AsInitialRemotePath()
    {
        foreach (TransferKind kind in new[] { TransferKind.File, TransferKind.Folder })
        {
            var window = new TestMainWindow
            {
                NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
                    localPath: @"D:\downloads\payload.txt",
                    remotePath: "/srv/app")
            };

            await window.StartSidebarUploadForTest(
                profile: CreateNativeProfile(),
                sessionId: Guid.Parse("3165a397-02ad-4dd2-913a-1b98b20ae7ef"),
                remoteDirectory: "/srv/app",
                kind: kind);

            Assert.NotNull(window.LastTransferDialogRequest);
            Assert.Equal("/srv/app", window.LastTransferDialogRequest!.RemotePath);
            Assert.True(window.LastTransferDialogRequest.PreferLocalPathOnOpen);
            Assert.Equal(kind, window.LastTransferDialogRequest.Kind);
            Assert.NotNull(window.QueuedJob);
            Assert.Equal("/srv/app", window.QueuedJob!.RemotePath);
            Assert.Equal(kind, window.QueuedJob.Kind);
        }
    }

    [AvaloniaFact]
    public async Task ManualTransferFlow_StillUsesProfileDefaultRemotePath()
    {
        var window = new TestMainWindow
        {
            NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
                localPath: @"D:\downloads\sample.txt",
                remotePath: "~/downloads")
        };

        TerminalProfile profile = CreateNativeProfile();

        await window.InitiateSftpTransferForTest(
            profile,
            Guid.Parse("5196f302-0ebe-4cdf-9777-4fe5bc1fa60a"),
            TransferDirection.Upload,
            TransferKind.File);

        Assert.NotNull(window.LastTransferDialogRequest);
        Assert.Equal(profile.DefaultRemoteDir, window.LastTransferDialogRequest!.RemotePath);
        Assert.False(window.LastTransferDialogRequest.PreferLocalPathOnOpen);
    }

    private static TerminalProfile CreateNativeProfile()
    {
        return new TerminalProfile
        {
            Id = Guid.Parse("4f13d6d8-ea72-4d88-9430-fc5d5f2490c7"),
            Name = "server3",
            Type = ConnectionType.SSH,
            SshBackendKind = NovaTerminal.Core.Ssh.Models.SshBackendKind.Native,
            DefaultRemoteDir = "~/downloads"
        };
    }

    private sealed class TestMainWindow : NovaTerminal.MainWindow
    {
        public TransferDialogResult? NextTransferDialogResult { get; set; }
        public TransferJob? QueuedJob { get; private set; }
        public TransferDialogRequest? LastTransferDialogRequest { get; private set; }

        internal override Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            LastTransferDialogRequest = request;
            return Task.FromResult(NextTransferDialogResult);
        }

        internal override void EnqueueTransferJob(TransferJob job)
        {
            QueuedJob = job;
        }
    }
}
