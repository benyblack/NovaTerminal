using System.Collections.Concurrent;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;
using NovaTerminal.Platform.Ssh.Sessions;

namespace NovaTerminal.Platform.Tests.Ssh;

public sealed class JumpHostConnectPlanTests
{
    [Fact]
    public void Create_FromProfileWithoutJumpHops_UsesDirectPlan()
    {
        JumpHostConnectPlan plan = JumpHostConnectPlan.Create(CreateProfile());

        Assert.False(plan.HasJumpHops);
        Assert.Empty(plan.JumpHops);
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

        Assert.True(plan.HasJumpHops);
        SshJumpHop hop = Assert.Single(plan.JumpHops);
        Assert.Equal("jump.internal", hop.Host);
        Assert.Equal("ops", hop.User);
        Assert.Equal(2200, hop.Port);
        Assert.Equal("target.internal", plan.TargetHost);
    }

    [Fact]
    public void Create_FromProfileWithMultipleJumpHops_PreservesTheChainInOrder()
    {
        // Order is the whole contract: the first hop is the only one reached over raw TCP, every
        // later hop is tunnelled through the one before it. A reordered chain connects through
        // the wrong bastions or not at all.
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal", User = "ops", Port = 2200 });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });

        JumpHostConnectPlan plan = JumpHostConnectPlan.Create(profile);

        Assert.True(plan.HasJumpHops);
        Assert.Equal(2, plan.JumpHops.Count);
        Assert.Equal("jump-one.internal", plan.JumpHops[0].Host);
        Assert.Equal(2200, plan.JumpHops[0].Port);
        Assert.Equal("jump-two.internal", plan.JumpHops[1].Host);
        Assert.Equal(22, plan.JumpHops[1].Port);
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
        SshJumpHop hop = Assert.Single(interop.LastConnectOptions!.JumpHops);
        Assert.Equal("jump.internal", hop.Host);
        Assert.Equal("ops", hop.User);
        Assert.Equal(2222, hop.Port);
        Assert.Contains(logs, message => message.Contains("backend=native", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, message => message.Contains("path=jump-host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NativeSession_WithMultipleJumpHops_ConnectsWithTheWholeChainAndLogsIt()
    {
        var interop = new CapturingNativeSshInterop();
        var logs = new ConcurrentQueue<string>();
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal", User = "ops", Port = 2200 });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });

        using var session = new NativeSshSession(profile, interop: interop, log: logs.Enqueue);

        await WaitUntilAsync(() => interop.LastConnectOptions != null);

        Assert.NotNull(interop.LastConnectOptions);
        IReadOnlyList<SshJumpHop> hops = interop.LastConnectOptions!.JumpHops;
        Assert.Equal(2, hops.Count);
        Assert.Equal("jump-one.internal", hops[0].Host);
        Assert.Equal("ops", hops[0].User);
        Assert.Equal(2200, hops[0].Port);
        Assert.Equal("jump-two.internal", hops[1].Host);
        // A hop without its own user runs as the target user — OpenSSH's default for a bare -J entry.
        Assert.Equal("nova", hops[1].User);
        Assert.Equal(22, hops[1].Port);
        Assert.Contains(logs, message => message.Contains("path=jump-chain:2", StringComparison.OrdinalIgnoreCase));
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

        public NovaSshSafeHandle Connect(NativeSshConnectionOptions options)
        {
            LastConnectOptions = options;
            return new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false);
        }

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle) => NativeSshEvent.Closed();

        public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows)
        {
        }

        public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options) => 1;

        public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
        }

        public void Close(NovaSshSafeHandle sessionHandle)
        {
        }
    }
}
