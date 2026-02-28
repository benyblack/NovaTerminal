using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class SettingsWindowProfileSeparationTests
{
    [Fact]
    public void BuildLocalProfilesForEditor_FiltersOutSshProfiles()
    {
        var localId = Guid.Parse("a6ed5c42-0464-469d-97f2-f87d2d2875bc");
        var source = new List<TerminalProfile>
        {
            new TerminalProfile
            {
                Id = localId,
                Name = "PowerShell",
                Type = ConnectionType.Local,
                Command = "pwsh.exe"
            },
            new TerminalProfile
            {
                Id = Guid.Parse("30f4c7d5-6d5c-43cb-b94a-23a7d331c883"),
                Name = "Prod SSH",
                Type = ConnectionType.SSH,
                SshHost = "prod.internal"
            }
        };

        List<TerminalProfile> result = SettingsWindow.BuildLocalProfilesForEditor(source);

        Assert.Single(result);
        Assert.Equal(localId, result[0].Id);
        Assert.Equal(ConnectionType.Local, result[0].Type);
        Assert.Equal("PowerShell", result[0].Name);
    }

    [Fact]
    public void NormalizeSettingsProfilesForSave_FiltersOutSshProfiles()
    {
        var source = new List<TerminalProfile>
        {
            new TerminalProfile
            {
                Id = Guid.Parse("9f64a4ec-b9f1-4ec6-a50f-5f8e3ff20d9d"),
                Name = "Bash",
                Type = ConnectionType.Local,
                Command = "/bin/bash"
            },
            new TerminalProfile
            {
                Id = Guid.Parse("d3516289-4525-4e5c-a7b3-1a3af09e0f0f"),
                Name = "Stage SSH",
                Type = ConnectionType.SSH,
                SshHost = "stage.internal"
            }
        };

        List<TerminalProfile> result = SettingsWindow.NormalizeSettingsProfilesForSave(source);

        Assert.Single(result);
        Assert.Equal(ConnectionType.Local, result[0].Type);
        Assert.Equal("Bash", result[0].Name);
    }

    [Fact]
    public void ResolveDefaultLocalProfileId_FallsBackToFirstLocalWhenCurrentMissing()
    {
        var firstId = Guid.Parse("3f2b1878-cb5d-48d6-bb71-100b2ef5bf2e");
        var profiles = new List<TerminalProfile>
        {
            new TerminalProfile { Id = firstId, Name = "PowerShell", Type = ConnectionType.Local },
            new TerminalProfile { Id = Guid.Parse("24337edd-e9a8-43f5-b8fd-8cd8ce7cd77a"), Name = "Bash", Type = ConnectionType.Local }
        };

        Guid resolved = SettingsWindow.ResolveDefaultLocalProfileId(Guid.Parse("6a5d526d-6d5e-4e20-b2d9-3f1443d5a56c"), profiles);

        Assert.Equal(firstId, resolved);
    }
}
