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
        Assert.True(window.TransferDialogWasShown);
    }

    [AvaloniaFact]
    public async Task SidebarDownload_UsesSelectedRemoteEntry_AsTransferRemotePath()
    {
        var window = new TestMainWindow
        {
            NextPickedLocalFolderPath = @"D:\downloads\logs"
        };

        await window.StartSidebarDownloadForTest(
            profile: CreateNativeProfile(),
            sessionId: Guid.Parse("2af2fbad-a4d3-4aeb-bf1d-e8884c0ad6f0"),
            selectedRemotePath: "/srv/logs",
            kind: TransferKind.Folder);

        Assert.NotNull(window.QueuedJob);
        Assert.Equal("/srv/logs", window.QueuedJob!.RemotePath);
        Assert.Equal(@"D:\downloads\logs", window.QueuedJob.LocalPath);
        Assert.Equal(TransferKind.Folder, window.QueuedJob.Kind);
        Assert.True(window.FolderPickerWasShown);
        Assert.False(window.TransferDialogWasShown);
    }

    [AvaloniaFact]
    public async Task SidebarDownloadFile_UsesSavePicker_WithRemoteFileName()
    {
        var window = new TestMainWindow
        {
            NextSavedLocalFilePath = @"D:\downloads\logs.txt"
        };

        await window.StartSidebarDownloadForTest(
            profile: CreateNativeProfile(),
            sessionId: Guid.Parse("2a6228c7-fc68-46f6-b620-60650ee2f4d0"),
            selectedRemotePath: "/srv/logs.txt",
            kind: TransferKind.File);

        Assert.NotNull(window.QueuedJob);
        Assert.Equal("/srv/logs.txt", window.QueuedJob!.RemotePath);
        Assert.Equal(@"D:\downloads\logs.txt", window.QueuedJob.LocalPath);
        Assert.Equal("logs.txt", window.LastSuggestedSaveFileName);
        Assert.True(window.SavePickerWasShown);
        Assert.False(window.TransferDialogWasShown);
    }

    [AvaloniaFact]
    public async Task SidebarUploadFile_UsesLocalFilePicker_AndCurrentRemoteDirectory()
    {
        var window = new TestMainWindow
        {
            NextPickedLocalFilePath = @"D:\temp\report.txt"
        };

        await window.StartSidebarUploadForTest(
            profile: CreateNativeProfile(),
            sessionId: Guid.Parse("3165a397-02ad-4dd2-913a-1b98b20ae7ef"),
            remoteDirectory: "/srv/app",
            kind: TransferKind.File);

        Assert.NotNull(window.QueuedJob);
        Assert.Equal("/srv/app", window.QueuedJob!.RemotePath);
        Assert.Equal(@"D:\temp\report.txt", window.QueuedJob.LocalPath);
        Assert.Equal(TransferKind.File, window.QueuedJob.Kind);
        Assert.True(window.FilePickerWasShown);
        Assert.False(window.FolderPickerWasShown);
        Assert.False(window.TransferDialogWasShown);
    }

    [AvaloniaFact]
    public async Task SidebarUploadFolder_UsesLocalFolderPicker_AndCurrentRemoteDirectory()
    {
        var window = new TestMainWindow
        {
            NextPickedLocalFolderPath = @"D:\temp\payload"
        };

        await window.StartSidebarUploadForTest(
            profile: CreateNativeProfile(),
            sessionId: Guid.Parse("f619f165-642b-4f83-9fef-785ca3473af5"),
            remoteDirectory: "/srv/app",
            kind: TransferKind.Folder);

        Assert.NotNull(window.QueuedJob);
        Assert.Equal("/srv/app", window.QueuedJob!.RemotePath);
        Assert.Equal(@"D:\temp\payload", window.QueuedJob.LocalPath);
        Assert.Equal(TransferKind.Folder, window.QueuedJob.Kind);
        Assert.False(window.FilePickerWasShown);
        Assert.True(window.FolderPickerWasShown);
        Assert.False(window.TransferDialogWasShown);
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

    [AvaloniaFact]
    public async Task ManualTransferFlow_StillUsesTransferDialog_WhenSidebarFlowDoesNot()
    {
        var window = new TestMainWindow
        {
            NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
                localPath: @"D:\downloads\logs",
                remotePath: "/srv/logs")
        };

        await window.InitiateSftpTransferForTest(
            CreateNativeProfile(),
            Guid.Parse("f3cbca88-8445-4e12-8e31-aa2b7f6d9f76"),
            TransferDirection.Download,
            TransferKind.Folder);

        Assert.True(window.TransferDialogWasShown);
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
        public string? NextPickedLocalFilePath { get; set; }
        public string? NextPickedLocalFolderPath { get; set; }
        public string? NextSavedLocalFilePath { get; set; }
        public TransferJob? QueuedJob { get; private set; }
        public TransferDialogRequest? LastTransferDialogRequest { get; private set; }
        public bool TransferDialogWasShown { get; private set; }
        public bool FilePickerWasShown { get; private set; }
        public bool FolderPickerWasShown { get; private set; }
        public bool SavePickerWasShown { get; private set; }
        public string? LastSuggestedSaveFileName { get; private set; }

        internal override Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            LastTransferDialogRequest = request;
            TransferDialogWasShown = true;
            return Task.FromResult(NextTransferDialogResult);
        }

        internal override Task<string?> PickLocalUploadFilePathAsync()
        {
            FilePickerWasShown = true;
            return Task.FromResult<string?>(NextPickedLocalFilePath);
        }

        internal override Task<string?> PickLocalUploadFolderPathAsync()
        {
            FolderPickerWasShown = true;
            return Task.FromResult<string?>(NextPickedLocalFolderPath);
        }

        internal override Task<string?> PickLocalDownloadFilePathAsync(string suggestedFileName)
        {
            SavePickerWasShown = true;
            LastSuggestedSaveFileName = suggestedFileName;
            return Task.FromResult<string?>(NextSavedLocalFilePath);
        }

        internal override Task<string?> PickLocalDownloadFolderPathAsync()
        {
            FolderPickerWasShown = true;
            return Task.FromResult<string?>(NextPickedLocalFolderPath);
        }

        internal override void EnqueueTransferJob(TransferJob job)
        {
            QueuedJob = job;
        }
    }
}
