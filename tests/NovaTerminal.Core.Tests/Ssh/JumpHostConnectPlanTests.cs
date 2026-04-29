using System.Collections.Concurrent;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Sessions;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class JumpHostConnectPlanTests
{
    [Fact]
    public void Create_FromProfileWithoutJumpHops_UsesDirectPlan()
    {
        JumpHostConnectPlan plan = JumpHostConnectPlan.Create(CreateProfile());

        Assert.False(plan.HasJumpHost);
        Assert.Null(plan.JumpHost);
        Assert.Equal("target.internal", plan.TargetHost);
        Assert.Equal(22, plan.TargetPort);
        Assert.Equal("nova", plan.TargetUser);
    }

    [Fact]
    public void Create_FromProfileWithOneJumpHop_UsesSingleHopPlan()
    {
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop
        {
            Host = "jump.internal",
            User = "ops",
            Port = 2200
        });

        JumpHostConnectPlan plan = JumpHostConnectPlan.Create(profile);

        Assert.True(plan.HasJumpHost);
        Assert.NotNull(plan.JumpHost);
        Assert.Equal("jump.internal", plan.JumpHost!.Host);
        Assert.Equal("ops", plan.JumpHost.User);
        Assert.Equal(2200, plan.JumpHost.Port);
        Assert.Equal("target.internal", plan.TargetHost);
    }

    [Fact]
    public void Create_FromProfileWithMultipleJumpHops_ThrowsClearUnsupportedError()
    {
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal" });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => JumpHostConnectPlan.Create(profile));

        Assert.Contains("Multiple jump hops", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeSession_WithOneJumpHop_ConnectsUsingJumpPlanAndLogsPath()
    {
        var interop = new CapturingNativeSshInterop();
        var logs = new ConcurrentQueue<string>();
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop
        {
            Host = "jump.internal",
            User = "ops",
            Port = 2222
        });

        using var session = new NativeSshSession(profile, interop: interop, log: logs.Enqueue);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        Assert.NotNull(interop.LastConnectOptions!.JumpHost);
        Assert.Equal("jump.internal", interop.LastConnectOptions.JumpHost!.Host);
        Assert.Equal("ops", interop.LastConnectOptions.JumpHost.User);
        Assert.Equal(2222, interop.LastConnectOptions.JumpHost.Port);
        Assert.Contains(logs, message => message.Contains("backend=native", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, message => message.Contains("path=jump-host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeSession_WithMultipleJumpHops_ThrowsClearUnsupportedError()
    {
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal" });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => new NativeSshSession(profile, interop: new CapturingNativeSshInterop()));

        Assert.Contains("Multiple jump hops", ex.Message, StringComparison.Ordinal);
    }

    private static SshProfile CreateProfile()
    {
        return new SshProfile
        {
            Id = Guid.Parse("22c57d51-794f-4df3-8a13-314a789ca829"),
            BackendKind = SshBackendKind.Native,
            Host = "target.internal",
            User = "nova",
            Port = 22
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    private sealed class CapturingNativeSshInterop : INativeSshInterop
    {
        public NativeSshConnectionOptions? LastConnectOptions { get; private set; }

        public IntPtr Connect(NativeSshConnectionOptions options)
        {
            LastConnectOptions = options;
            return new IntPtr(1);
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public NativeSshEvent? PollEvent(IntPtr sessionHandle) => NativeSshEvent.Closed();

        public void Write(IntPtr sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(IntPtr sessionHandle, int cols, int rows)
        {
        }

        public int OpenDirectTcpIp(IntPtr sessionHandle, NativePortForwardOpenOptions options) => 1;

        public void WriteChannel(IntPtr sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(IntPtr sessionHandle, int channelId)
        {
        }

        public void CloseChannel(IntPtr sessionHandle, int channelId)
        {
        }

        public void SubmitResponse(IntPtr sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public void Close(IntPtr sessionHandle)
        {
        }
    }
}
