using NovaTerminal.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Core;

public sealed class TerminalPaneRemoteFilesSidebarTests
{
    [AvaloniaFact]
    public void RemoteFilesSidebarHost_IsCreatedOnlyWhenOpened()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova"
        });

        var presenter = pane.FindControl<ContentControl>("RemoteFilesSidebarPresenter");

        Assert.NotNull(presenter);
        Assert.Null(presenter!.Content);

        pane.ShowRemoteFilesSidebarForTest();

        Assert.IsType<RemoteFilesSidebar>(presenter.Content);
    }

    [AvaloniaFact]
    public void NativeSshPane_ContextMenu_KeepsOnlyRemoteFilesEntry()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova"
        });

        IReadOnlyList<string> names = pane.GetSftpContextMenuItemNamesForTest();

        Assert.Equal(new[] { "MenuToggleRemoteFilesSidebar" }, names);
    }

    [AvaloniaFact]
    public void Sidebar_HidesImmediately_WhenAltScreenBecomesActive()
    {
        var pane = new TerminalPane();

        pane.ShowRemoteFilesSidebarForTest();
        pane.HandleAltScreenChangedForTest(true);

        Assert.False(pane.IsRemoteFilesSidebarVisibleForTest());
    }

    [AvaloniaFact]
    public void SidebarEntryPoint_IsUnavailable_ForLocalPane()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "PowerShell",
            Type = ConnectionType.Local,
            Command = "pwsh.exe"
        });

        Assert.False(pane.IsRemoteFilesSidebarEntryAvailableForTest());
    }

    [AvaloniaFact]
    public void SidebarEntryPoint_IsUnavailable_ForOpenSshPane()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "OpenSSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.OpenSsh,
            SshHost = "server.example",
            SshUser = "nova"
        });

        Assert.False(pane.IsRemoteFilesSidebarEntryAvailableForTest());
    }

    [AvaloniaFact]
    public void OpeningSidebar_PrefersPaneWorkingDirectory_OverProfileDefault()
    {
        var service = new RecordingRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("/srv/app", Array.Empty<RemoteSidebarEntry>()));
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova",
            DefaultRemoteDir = "~/downloads"
        });

        pane.ConfigureRemoteFilesSidebarForTest(service);
        pane.HandleWorkingDirectoryChangedForTest("/srv/app");

        pane.ShowRemoteFilesSidebarForTest();

        Assert.Equal(new[] { "/srv/app" }, service.RequestedPaths);
        Assert.Equal("/srv/app", pane.GetRemoteFilesSidebarCurrentPathForTest());
    }

    [AvaloniaFact]
    public void OpeningSidebar_FallsBackToProfileDefault_WhenPaneWorkingDirectoryIsMissing()
    {
        var service = new RecordingRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("~/downloads", Array.Empty<RemoteSidebarEntry>()));
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova",
            DefaultRemoteDir = "~/downloads"
        });

        pane.ConfigureRemoteFilesSidebarForTest(service);

        pane.ShowRemoteFilesSidebarForTest();

        Assert.Equal(new[] { "~/downloads" }, service.RequestedPaths);
        Assert.Equal("~/downloads", pane.GetRemoteFilesSidebarCurrentPathForTest());
    }

    [AvaloniaFact]
    public void WorkingDirectoryChanged_UpdatesJumpAffordance_WithoutForcingNavigation()
    {
        var service = new RecordingRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("~/downloads", Array.Empty<RemoteSidebarEntry>()));
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova",
            DefaultRemoteDir = "~/downloads"
        });

        pane.ConfigureRemoteFilesSidebarForTest(service);
        pane.ShowRemoteFilesSidebarForTest();

        pane.HandleWorkingDirectoryChangedForTest("/srv/app");

        Assert.Equal("~/downloads", pane.GetRemoteFilesSidebarCurrentPathForTest());
        Assert.Equal("/srv/app", pane.GetRemoteFilesSidebarJumpTargetForTest());
        Assert.Equal(new[] { "~/downloads" }, service.RequestedPaths);
    }

    [AvaloniaFact]
    public void SessionExit_KeepsSidebarVisibleInDisconnectedState()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "Native SSH",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "server.example",
            SshUser = "nova",
            DefaultRemoteDir = "~/downloads"
        });

        pane.ConfigureRemoteFilesSidebarForTest(new RecordingRemoteDirectoryBrowserService(
            RemoteSidebarListingResult.Success("~/downloads", Array.Empty<RemoteSidebarEntry>())));
        pane.ShowRemoteFilesSidebarForTest();

        pane.HandleSessionExitForTesting(255);

        Assert.True(pane.IsRemoteFilesSidebarVisibleForTest());
        Assert.True(pane.IsRemoteFilesSidebarDisconnectedForTest());
        Assert.False(pane.IsRemoteFilesSidebarEntryAvailableForTest());
    }

    private sealed class RecordingRemoteDirectoryBrowserService : IRemoteDirectoryBrowserService
    {
        private readonly Queue<RemoteSidebarListingResult> _results;

        public RecordingRemoteDirectoryBrowserService(params RemoteSidebarListingResult[] results)
        {
            _results = new Queue<RemoteSidebarListingResult>(results);
        }

        public List<string> RequestedPaths { get; } = new();

        public Task<RemoteSidebarListingResult> ListDirectoryAsync(
            Guid profileId,
            Guid sessionId,
            string remotePath,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(remotePath);

            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No more sidebar results configured.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }
}
