using NovaTerminal.Core;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Core;

public sealed class SftpServiceTests
{
    [Fact]
    public void ResolveProfileForJob_PrefersProfileIdOverDuplicateName()
    {
        var localProfiles = new List<TerminalProfile>
        {
            new TerminalProfile
            {
                Id = Guid.Parse("8f2f3697-a47c-49e8-a58d-88fd3f5e33c1"),
                Name = "Prod",
                Type = ConnectionType.SSH,
                SshHost = "legacy.internal"
            }
        };

        var firstStoreProfile = new TerminalProfile
        {
            Id = Guid.Parse("24f8d9af-3116-4d4f-a522-5b1411c303b9"),
            Name = "Prod",
            Type = ConnectionType.SSH,
            SshHost = "a.internal"
        };

        var secondStoreProfile = new TerminalProfile
        {
            Id = Guid.Parse("1204d573-7d39-49ca-80d8-b8e1df5d2052"),
            Name = "Prod",
            Type = ConnectionType.SSH,
            SshHost = "b.internal"
        };

        var storeProfiles = new List<TerminalProfile> { firstStoreProfile, secondStoreProfile };
        var job = new TransferJob
        {
            ProfileId = secondStoreProfile.Id,
            ProfileName = "Prod",
            Direction = TransferDirection.Upload,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\file.txt",
            RemotePath = "/tmp/file.txt"
        };

        TerminalProfile? resolved = SftpService.ResolveProfileForJob(job, localProfiles, storeProfiles);

        Assert.NotNull(resolved);
        Assert.Equal(secondStoreProfile.Id, resolved!.Id);
        Assert.Equal("b.internal", resolved.SshHost);
    }

    [Fact]
    public void BuildScpArguments_UsesGeneratedConfigAliasWhenAvailable()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("f20d42d7-44d8-493e-a6ed-d2f52fa89f4a"),
            Name = "Prod",
            Type = ConnectionType.SSH,
            SshHost = "prod.internal",
            SshUser = "ops",
            SshPort = 2222
        };

        var details = new SshLaunchDetails
        {
            SshPath = "ssh.exe",
            ConfigPath = @"C:\Users\me\.ssh\ssh_config.generated",
            Alias = "nova_abc123",
            CommandLine = "ssh ...",
        };

        var job = new TransferJob
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = TransferDirection.Upload,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\local.txt",
            RemotePath = "/tmp/remote.txt"
        };

        string args = SftpService.BuildScpArguments(job, profile, details);

        Assert.Contains(" -F ", args, StringComparison.Ordinal);
        Assert.Contains("ssh_config.generated", args, StringComparison.Ordinal);
        Assert.Contains("nova_abc123:", args, StringComparison.Ordinal);
        Assert.DoesNotContain(" -J ", args, StringComparison.Ordinal);
    }
}
