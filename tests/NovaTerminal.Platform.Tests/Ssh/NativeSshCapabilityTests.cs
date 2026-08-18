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
    public void Evaluate_ForForwardKindsTheNativeBackendImplements_IsSupported(PortForwardKind kind)
    {
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = kind, SourcePort = 15000 });

        Assert.True(NativeSshCapability.Evaluate(profile).IsSupported);
    }

    [Fact]
    public void Evaluate_ForRemoteForward_IsUnsupportedAndNamesBothFixes()
    {
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Remote,
            SourcePort = 8080,
            DestinationHost = "svc.internal",
            DestinationPort = 80
        });

        NativeSshCapabilityResult result = NativeSshCapability.Evaluate(profile);

        Assert.False(result.IsSupported);
        Assert.Equal(NativeSshUnsupportedReason.RemotePortForward, result.Reason);
        Assert.Contains("remote forward", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenSSH", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ForRemoteForwardAmongSupportedOnes_StillReportsUnsupported()
    {
        SshProfile profile = CreateProfile();
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Local, SourcePort = 15000, DestinationHost = "a", DestinationPort = 1 });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Remote, SourcePort = 15001, DestinationHost = "b", DestinationPort = 2 });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Dynamic, SourcePort = 15002 });

        Assert.Equal(NativeSshUnsupportedReason.RemotePortForward, NativeSshCapability.Evaluate(profile).Reason);
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
    public void Evaluate_ForRemoteForwardBehindAJumpChain_StillReportsTheForward()
    {
        // The chain is no longer a problem, so the remote forward must be what gets reported —
        // not masked by hops that are now fine.
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal" });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Remote, SourcePort = 8080, DestinationHost = "svc", DestinationPort = 80 });

        Assert.Equal(NativeSshUnsupportedReason.RemotePortForward, NativeSshCapability.Evaluate(profile).Reason);
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
