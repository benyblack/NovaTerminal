using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Core;

public sealed class SftpServiceTests
{
    [Fact]
    public void SshAskPassCommand_IsSupportedCliMode_WhenEnvironmentFlagIsSet()
    {
        string? previous = Environment.GetEnvironmentVariable(SshAskPassCommand.ModeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SshAskPassCommand.ModeEnvironmentVariable, "1");

            Assert.True(SshAskPassCommand.IsSupportedCliMode(Array.Empty<string>()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SshAskPassCommand.ModeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void SelectTransferBackend_ForNativeProfile_UsesNativeSftp()
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native
        };

        Assert.Equal(SftpTransferBackend.NativeSftp, SftpService.SelectTransferBackend(profile));
    }

    [Fact]
    public void SelectTransferBackend_ForOpenSshProfile_UsesExternalScp()
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.OpenSsh
        };

        Assert.Equal(SftpTransferBackend.ExternalScp, SftpService.SelectTransferBackend(profile));
    }

    [Fact]
    public void BuildNativeTransferConnectionOptions_UsesProfileTarget()
    {
        var service = new SshConnectionService(new InMemorySshProfileStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops",
            SshPort = 2200
        };

        NativeSshConnectionOptions options = SftpService.BuildNativeTransferConnectionOptions(service, profile);

        Assert.Equal("prod.internal", options.Host);
        Assert.Equal("ops", options.User);
        Assert.Equal(2200, options.Port);
    }

    [Fact]
    public void BuildNativeTransferConnectionOptions_UsesStoredProfileWhenAvailable()
    {
        Guid profileId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var store = new InMemorySshProfileStore();
        store.SaveProfile(new SshProfile
        {
            Id = profileId,
            Name = "Prod",
            BackendKind = SshBackendKind.Native,
            Host = "prod.internal",
            User = "ops",
            Port = 2200,
            ServerAliveIntervalSeconds = 45,
            ServerAliveCountMax = 6,
            JumpHops = new List<SshJumpHop>
            {
                new() { Host = "jump.internal", User = "jumper", Port = 2222 }
            }
        });
        var service = new SshConnectionService(store);
        TerminalProfile runtimeProfile = service.GetConnectionProfiles().Single(profile => profile.Id == profileId);

        NativeSshConnectionOptions options = SftpService.BuildNativeTransferConnectionOptions(service, runtimeProfile);

        Assert.Equal("prod.internal", options.Host);
        Assert.Equal("ops", options.User);
        Assert.Equal(2200, options.Port);
        Assert.Equal(45, options.KeepAliveIntervalSeconds);
        Assert.Equal(6, options.KeepAliveCountMax);
        Assert.NotNull(options.JumpHost);
        Assert.Equal("jump.internal", options.JumpHost!.Host);
        Assert.Equal("jumper", options.JumpHost.User);
        Assert.Equal(2222, options.JumpHost.Port);
    }

    [Fact]
    public void BuildNativeTransferConnectionOptions_ThrowsWhenJumpHostFallbackHasNoProfileList()
    {
        var service = new SshConnectionService(new InMemorySshProfileStore());
        var targetProfile = new TerminalProfile
        {
            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops",
            JumpHostProfileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SftpService.BuildNativeTransferConnectionOptions(service, targetProfile));

        Assert.Contains("allProfiles", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNativeTransferConnectionOptions_UsesResolvedJumpHost()
    {
        var service = new SshConnectionService(new InMemorySshProfileStore());
        Guid jumpId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var jumpProfile = new TerminalProfile
        {
            Id = jumpId,
            Type = ConnectionType.SSH,
            SshHost = "jump.internal",
            SshUser = "jumper",
            SshPort = 2222
        };
        var targetProfile = new TerminalProfile
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccce"),
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops",
            JumpHostProfileId = jumpId
        };

        NativeSshConnectionOptions options = SftpService.BuildNativeTransferConnectionOptions(service, targetProfile, new[] { jumpProfile, targetProfile });

        Assert.NotNull(options.JumpHost);
        Assert.Equal("jump.internal", options.JumpHost!.Host);
        Assert.Equal("jumper", options.JumpHost.User);
        Assert.Equal(2222, options.JumpHost.Port);
    }

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
        Assert.Contains(" -O ", args, StringComparison.Ordinal);
        Assert.DoesNotContain(" -J ", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScpArguments_ForDownload_UsesLegacyScpProtocol()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("81e8e7e4-4034-4c4b-9cc4-d0894a465d9d"),
            Name = "Prod",
            Type = ConnectionType.SSH,
            SshHost = "prod.internal",
            SshUser = "ops"
        };

        var job = new TransferJob
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\remote.txt",
            RemotePath = "/tmp/remote.txt"
        };

        string args = SftpService.BuildScpArguments(job, profile, launchDetails: null);

        Assert.Contains(" -O ", args, StringComparison.Ordinal);
        Assert.Contains("ops@prod.internal:", args, StringComparison.Ordinal);
        Assert.Matches(@"ops@prod\.internal:""/tmp/remote\.txt""\s+""C:/tmp/remote\.txt""$", args);
    }

    [Fact]
    public void BuildScpArguments_ForNativeBackend_AllowsInteractiveAuthentication()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("39292451-ad10-4f26-aeb7-2175527a66be"),
            Name = "Native Prod",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops"
        };

        var job = new TransferJob
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\remote.txt",
            RemotePath = "/tmp/remote.txt"
        };

        string args = SftpService.BuildScpArguments(job, profile, launchDetails: null);

        Assert.Contains(" -O ", args, StringComparison.Ordinal);
        Assert.DoesNotContain(" -B ", args, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateScpStartInfo_ForNativeBackend_UsesAskPassAndStaysHidden()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"),
            Name = "Native Prod",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops",
            SshPort = 2200
        };

        var startInfo = SftpService.CreateScpStartInfo("scp.exe", "-O source target", profile);

        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("1", startInfo.Environment[SshAskPassCommand.ModeEnvironmentVariable]);
        Assert.Equal("force", startInfo.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.False(string.IsNullOrWhiteSpace(startInfo.Environment["SSH_ASKPASS"]));
        Assert.Equal(profile.Id.ToString(), startInfo.Environment[SshAskPassCommand.ProfileIdEnvironmentVariable]);
        Assert.Equal("Native Prod", startInfo.Environment[SshAskPassCommand.ProfileNameEnvironmentVariable]);
        Assert.Equal("ops", startInfo.Environment[SshAskPassCommand.ProfileUserEnvironmentVariable]);
        Assert.Equal("prod.internal", startInfo.Environment[SshAskPassCommand.ProfileHostEnvironmentVariable]);
        Assert.Equal("2200", startInfo.Environment[SshAskPassCommand.ProfilePortEnvironmentVariable]);
    }

    [Fact]
    public void CreateScpStartInfo_ForOpenSshBackend_StaysHiddenAndCapturesErrors()
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.OpenSsh
        };

        var startInfo = SftpService.CreateScpStartInfo("scp.exe", "-O -B source target", profile);

        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
    }

    private sealed class InMemorySshProfileStore : ISshProfileStore
    {
        private readonly Dictionary<Guid, SshProfile> _profiles = new();

        public IReadOnlyList<SshProfile> GetProfiles() => _profiles.Values.ToArray();

        public SshProfile? GetProfile(Guid profileId) => _profiles.GetValueOrDefault(profileId);

        public void SaveProfile(SshProfile profile)
        {
            _profiles[profile.Id] = profile;
        }

        public bool DeleteProfile(Guid profileId) => _profiles.Remove(profileId);
    }
}
