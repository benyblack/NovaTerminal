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

    private sealed class TestMainWindow : NovaTerminal.MainWindow
    {
        public TransferDialogResult? NextTransferDialogResult { get; set; }
        public TransferJob? QueuedJob { get; private set; }

        internal override Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            return Task.FromResult(NextTransferDialogResult);
        }

        internal override void EnqueueTransferJob(TransferJob job)
        {
            QueuedJob = job;
        }
    }
}
