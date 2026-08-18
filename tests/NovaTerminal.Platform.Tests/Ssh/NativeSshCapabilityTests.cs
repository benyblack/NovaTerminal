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

    [Fact]
    public void Evaluate_ForSingleJumpHop_IsSupported()
    {
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump.internal" });

        Assert.True(NativeSshCapability.Evaluate(profile).IsSupported);
    }

    [Fact]
    public void Evaluate_ForJumpHopChain_IsUnsupportedWithTheEstablishedWording()
    {
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal" });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });

        NativeSshCapabilityResult result = NativeSshCapability.Evaluate(profile);

        Assert.False(result.IsSupported);
        Assert.Equal(NativeSshUnsupportedReason.MultipleJumpHops, result.Reason);
        // JumpHostConnectPlan surfaces this same string; its test pins the prefix.
        Assert.Contains("Multiple jump hops", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenBothProblemsPresent_ReportsTheHopChainFirst()
    {
        SshProfile profile = CreateProfile();
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-one.internal" });
        profile.JumpHops.Add(new SshJumpHop { Host = "jump-two.internal" });
        profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Remote, SourcePort = 8080, DestinationHost = "svc", DestinationPort = 80 });

        Assert.Equal(NativeSshUnsupportedReason.MultipleJumpHops, NativeSshCapability.Evaluate(profile).Reason);
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
