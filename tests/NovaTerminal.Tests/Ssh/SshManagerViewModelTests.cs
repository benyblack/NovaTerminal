using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class SshManagerViewModelTests
{
    [Fact]
    public void Search_FiltersByNameHostUserTagsGroupAndNotes()
    {
        var vm = new SshManagerViewModel();
        vm.LoadProfiles(new[]
        {
            new TerminalProfile
            {
                Id = Guid.Parse("0f4871f8-0f30-4ba0-8458-cf24072f7be0"),
                Type = ConnectionType.SSH,
                Name = "Prod DB",
                SshHost = "db.prod.internal",
                SshUser = "dba",
                Group = "Prod/DB",
                Notes = "primary database",
                Tags = new List<string> { "critical", "postgres" }
            },
            new TerminalProfile
            {
                Id = Guid.Parse("74a20bc4-1856-4c40-9790-a59fcb0f4d45"),
                Type = ConnectionType.SSH,
                Name = "Stage API",
                SshHost = "api.stage.internal",
                SshUser = "ops",
                Group = "Stage/API",
                Notes = "integration environment",
                Tags = new List<string> { "api" }
            }
        });

        vm.SearchText = "postgres";
        Assert.Single(vm.FilteredRows);

        vm.SearchText = "Prod/DB";
        Assert.Single(vm.FilteredRows);

        vm.SearchText = "primary database";
        Assert.Single(vm.FilteredRows);

        vm.SearchText = "ops";
        Assert.Single(vm.FilteredRows);
        Assert.Equal("Stage API", vm.FilteredRows[0].Name);
    }

    [Fact]
    public void Favorites_ArePinnedAtTopAndStable()
    {
        var vm = new SshManagerViewModel();
        vm.LoadProfiles(new[]
        {
            new TerminalProfile
            {
                Id = Guid.Parse("2f43c80a-5fc9-461b-a81e-c167815f0001"),
                Type = ConnectionType.SSH,
                Name = "Zulu",
                SshHost = "zulu.internal"
            },
            new TerminalProfile
            {
                Id = Guid.Parse("c5a0f68a-7277-44f1-a593-022dd5145d88"),
                Type = ConnectionType.SSH,
                Name = "Alpha",
                SshHost = "alpha.internal",
                Tags = new List<string> { "favorite" }
            }
        });

        Assert.Equal("Alpha", vm.FilteredRows[0].Name);
        Assert.Equal("Zulu", vm.FilteredRows[1].Name);

        vm.ToggleFavorite(vm.FilteredRows[1]);

        Assert.True(vm.FilteredRows[0].IsFavorite);
        Assert.True(vm.FilteredRows[1].IsFavorite);
        Assert.Equal("Alpha", vm.FilteredRows[0].Name);
        Assert.Equal("Zulu", vm.FilteredRows[1].Name);
    }

    [Fact]
    public void QuickOpen_RaisesRequestedAction()
    {
        var vm = new SshManagerViewModel();
        vm.LoadProfiles(new[]
        {
            new TerminalProfile
            {
                Id = Guid.Parse("82692845-ccf6-44ff-b8ee-38d77f3f19fd"),
                Type = ConnectionType.SSH,
                Name = "Prod",
                SshHost = "prod.internal"
            }
        });

        SshQuickOpenTarget? receivedTarget = null;
        SshDiagnosticsLevel? receivedLevel = null;
        vm.DiagnosticsLevel = SshDiagnosticsLevel.VeryVerbose;
        vm.OpenRequested += (_, target, level) =>
        {
            receivedTarget = target;
            receivedLevel = level;
        };

        vm.RequestOpen(vm.FilteredRows[0], SshQuickOpenTarget.SplitVertical);

        Assert.Equal(SshQuickOpenTarget.SplitVertical, receivedTarget);
        Assert.Equal(SshDiagnosticsLevel.VeryVerbose, receivedLevel);
    }
}
