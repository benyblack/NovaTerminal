using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;

namespace NovaTerminal.Platform.Tests.Ssh;

public sealed class NativeSshCapabilityTests
{
    [Fact]
    public void Evaluate_ForPlainProfile_IsSupported()
    {
        NativeSshCapabilityResult result = NativeSshCapability.Evaluate(CreateProfile());

        Assert.True(result.IsSupported);
        Assert.Equal(NativeSshUnsupportedReason.None, result.Reason);
        Assert.Equal(string.Empty, result.Explanation);
    }

    [Theory]
    [InlineData(PortForwardKind.Local)]
    [InlineData(PortForwardKind.Dynamic)]
    [InlineData(PortForwardKind.Remote)]
    public void Evaluate_ForForwardKindsTheNativeBackendImplements_IsSupported(PortForwardKind kind)
    {
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = kind, SourcePort = 15000 });

        Assert.True(NativeSshCapability.Evaluate(profile).IsSupported);
    }

    [Fact]
    public void Evaluate_ForEveryForwardKindTogether_IsSupported()
    {
        // Remote forwards were the gate's last refusal. With them served natively, a profile
        // mixing all three kinds — the shape that used to be refused for its remote member —
        // must evaluate as supported.
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Local, SourcePort = 15000, DestinationHost = "a", DestinationPort = 1 });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Remote, SourcePort = 15001, DestinationHost = "b", DestinationPort = 2 });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Dynamic, SourcePort = 15002 });

        NativeSshCapabilityResult result = NativeSshCapability.Evaluate(profile);

        Assert.True(result.IsSupported);
        Assert.Equal(NativeSshUnsupportedReason.None, result.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Evaluate_ForJumpHopChainsOfAnyLength_IsSupported(int hopCount)
    {
        // Chains of any length are served natively — one nested direct-tcpip hop per entry, the
        // way OpenSSH treats a -J chain. A profile that used to be refused for its second hop
        // must now save and connect.
        SshProfile profile = CreateProfile();
        for (int i = 0; i < hopCount; i++)
        {
            profile.JumpHops.Add(new SshJumpHop { Host = $"jump-{i}.internal" });
        }

        Assert.True(NativeSshCapability.Evaluate(profile).IsSupported);
    }

    [Fact]
    public void Evaluate_ForRemoteForwardBehindAJumpChain_IsSupported()
    {
        // The two shapes that were each the gate's reason to refuse at some point, combined —
        // both are served natively now (the gate's one refusal left is a remote forward with a
        // server-allocated port).
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal" });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Remote, SourcePort = 8080, DestinationHost = "svc", DestinationPort = 80 });

        Assert.True(NativeSshCapability.Evaluate(profile).IsSupported);
    }

    [Fact]
    public void Evaluate_ForRemoteForwardWithPortZero_IsRefusedByName()
    {
        // -R 0:... asks the server to allocate the listen port. The native backend routes
        // incoming connections back to their rule by the requested port, so a server-picked
        // port has no rule to land on — the gate must refuse the shape up front, not let the
        // request die in a background task's log (Codex review on #333).
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Remote, SourcePort = 0, DestinationHost = "svc", DestinationPort = 80 });

        NativeSshCapabilityResult result = NativeSshCapability.Evaluate(profile);

        Assert.False(result.IsSupported);
        Assert.Equal(NativeSshUnsupportedReason.RemoteForwardWithServerAllocatedPort, result.Reason);
        Assert.Contains("port 0", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("OpenSSH", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ForLocalOrDynamicForwardWithPortZero_IsSupported()
    {
        // Port 0 is only ambiguous for remote forwards: a local or dynamic listener binds an
        // ephemeral port on this machine, where the OS answers immediately and no rule matching
        // is involved.
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Local, SourcePort = 0, DestinationHost = "svc", DestinationPort = 80 });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Dynamic, SourcePort = 0 });

        Assert.True(NativeSshCapability.Evaluate(profile).IsSupported);
    }

    [Fact]
    public void Evaluate_WithNullCollections_IsSupported()
    {
        // The draft overload is called with whatever the editor holds; neither collection is required.
        Assert.True(NativeSshCapability.Evaluate(forwards: null, jumpHops: null).IsSupported);
    }

    [Fact]
    public void Evaluate_WithNullProfile_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NativeSshCapability.Evaluate((SshProfile)null!));
    }

    private static SshProfile CreateProfile() => new()
    {
        Id = Guid.Parse("2b0d2f6c-3f5b-4f0a-9a4f-0f0a5b7c9d11"),
        Name = "native",
        Host = "target.internal",
        User = "nova",
        BackendKind = SshBackendKind.Native
    };
}
