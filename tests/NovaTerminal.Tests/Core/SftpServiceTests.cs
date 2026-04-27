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
    public void SelectExecutionBackend_ForNativeFolder_UsesNativeSftp()
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native
        };
        var job = new TransferJob
        {
            Direction = TransferDirection.Download,
            Kind = TransferKind.Folder,
            LocalPath = @"C:\tmp\folder",
            RemotePath = "/tmp/folder"
        };

        Assert.Equal(SftpTransferBackend.NativeSftp, SftpService.SelectExecutionBackend(profile, job));
    }

    [Fact]
    public void ExecuteNativeSftpTransfer_UsesVaultPasswordKnownHostsAndInterop()
    {
        var service = new SshConnectionService(new InMemorySshProfileStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("01984d8a-2ab0-72c8-b66f-965f8491a6ae"),
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops",
            SshPort = 2200
        };
        var job = new TransferJob
        {
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\download.txt",
            RemotePath = "/tmp/download.txt"
        };
        var interop = new CapturingNativeSshInterop();

        SftpService.ExecuteNativeSftpTransfer(
            job,
            profile,
            service,
            allProfiles: null,
            interop: interop,
            passwordResolver: static _ => "vault-pass",
            knownHostsFilePath: @"C:\ssh\native_known_hosts.json");

        Assert.NotNull(interop.ConnectionOptions);
        Assert.NotNull(interop.TransferOptions);
        Assert.Equal("prod.internal", interop.ConnectionOptions!.Host);
        Assert.Equal("ops", interop.ConnectionOptions.User);
        Assert.Equal(2200, interop.ConnectionOptions.Port);
        Assert.Equal("vault-pass", interop.ConnectionOptions.Password);
        Assert.Equal(@"C:\ssh\native_known_hosts.json", interop.ConnectionOptions.KnownHostsFilePath);
        Assert.Equal(NativeSftpTransferDirection.Download, interop.TransferOptions!.Direction);
        Assert.Equal(NativeSftpTransferKind.File, interop.TransferOptions.Kind);
        Assert.Equal(@"C:\tmp\download.txt", interop.TransferOptions.LocalPath);
        Assert.Equal("/tmp/download.txt", interop.TransferOptions.RemotePath);
    }

    [Fact]
    public void ExecuteNativeSftpTransfer_PrefersIdentityFileOverVaultPassword()
    {
        Guid profileId = Guid.Parse("01984d8f-4c31-7ae9-b8f2-7de0093b5cf7");
        var store = new InMemorySshProfileStore();
        store.SaveProfile(new SshProfile
        {
            Id = profileId,
            Name = "Keyed",
            BackendKind = SshBackendKind.Native,
            Host = "prod.internal",
            User = "ops",
            Port = 2200,
            AuthMode = SshAuthMode.IdentityFile,
            IdentityFilePath = @"C:\keys\id_ed25519"
        });
        var service = new SshConnectionService(store);
        TerminalProfile profile = service.GetConnectionProfiles().Single(connection => connection.Id == profileId);
        var job = new TransferJob
        {
            Direction = TransferDirection.Upload,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\upload.txt",
            RemotePath = "/tmp/upload.txt"
        };
        var interop = new CapturingNativeSshInterop();

        SftpService.ExecuteNativeSftpTransfer(
            job,
            profile,
            service,
            allProfiles: null,
            interop: interop,
            passwordResolver: static _ => "stale-vault-password",
            knownHostsFilePath: @"C:\ssh\native_known_hosts.json");

        Assert.NotNull(interop.ConnectionOptions);
        Assert.Equal(@"C:\keys\id_ed25519", interop.ConnectionOptions!.IdentityFilePath);
        Assert.Null(interop.ConnectionOptions.Password);
    }

    [Fact]
    public void ExecuteNativeSftpTransfer_ForwardsCancellationTokenToInterop()
    {
        var service = new SshConnectionService(new InMemorySshProfileStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("01984da2-06b2-77a3-b8cd-cad971781eec"),
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.Native,
            SshHost = "prod.internal",
            SshUser = "ops",
            SshPort = 2200
        };
        var job = new TransferJob
        {
            Direction = TransferDirection.Download,
            Kind = TransferKind.File,
            LocalPath = @"C:\tmp\download.txt",
            RemotePath = "/tmp/download.txt"
        };
        var interop = new CapturingNativeSshInterop();
        using var cts = new CancellationTokenSource();

        SftpService.ExecuteNativeSftpTransfer(
            job,
            profile,
            service,
            allProfiles: null,
            interop: interop,
            passwordResolver: static _ => "vault-pass",
            knownHostsFilePath: @"C:\ssh\native_known_hosts.json",
            cancellationToken: cts.Token);

        Assert.True(interop.CancellationToken.CanBeCanceled);
    }

    [Fact]
    public void ApplyNativeTransferProgress_MapsBytesAndFraction()
    {
        var job = new TransferJob();
        var progress = new NativeSftpTransferProgress
        {
            BytesDone = 25,
            BytesTotal = 100,
            CurrentPath = "/tmp/file.txt"
        };

        SftpService.ApplyNativeTransferProgress(job, progress);

        Assert.Equal(25, job.BytesDone);
        Assert.Equal(100, job.BytesTotal);
        Assert.Equal(0.25, job.Progress, 3);
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
    public void BuildScpArguments_ForOpenSshBackend_UsesBatchMode()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("39292451-ad10-4f26-aeb7-2175527a66be"),
            Name = "OpenSSH Prod",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.OpenSsh,
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
        Assert.Contains(" -B ", args, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateScpStartInfo_DoesNotConfigureAskPass()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"),
            Name = "OpenSSH Prod",
            Type = ConnectionType.SSH,
            SshBackendKind = SshBackendKind.OpenSsh,
            SshHost = "prod.internal",
            SshUser = "ops",
            SshPort = 2200
        };

        var startInfo = SftpService.CreateScpStartInfo("scp.exe", "-O source target", profile);

        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.False(startInfo.Environment.ContainsKey("SSH_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("SSH_ASKPASS_REQUIRE"));
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

    private sealed class CapturingNativeSshInterop : INativeSshInterop
    {
        public NativeSshConnectionOptions? ConnectionOptions { get; private set; }
        public NativeSftpTransferOptions? TransferOptions { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public IntPtr Connect(NativeSshConnectionOptions options) => throw new NotSupportedException();

        public void RunSftpTransfer(
            NativeSshConnectionOptions connectionOptions,
            NativeSftpTransferOptions transferOptions,
            Action<NativeSftpTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            ConnectionOptions = connectionOptions;
            TransferOptions = transferOptions;
            CancellationToken = cancellationToken;
        }

        public NativeSshEvent? PollEvent(IntPtr sessionHandle) => throw new NotSupportedException();
        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Resize(IntPtr sessionHandle, int cols, int rows) => throw new NotSupportedException();
        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options) => throw new NotSupportedException();
        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void SendChannelEof(IntPtr sessionHandle, int channelId) => throw new NotSupportedException();
        public void CloseChannel(IntPtr sessionHandle, int channelId) => throw new NotSupportedException();
        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Close(IntPtr sessionHandle) => throw new NotSupportedException();
    }
}
