using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class ActiveSshSessionRegistryTests
{
    [Fact]
    public void TryGet_WhenRegisteredActiveNativeSessionExists_ReturnsDescriptor()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            profileId,
            SshBackendKind.Native));

        Assert.True(registry.TryGet(sessionId, out ActiveSshSessionDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(profileId, descriptor!.ProfileId);
        Assert.Equal(SshBackendKind.Native, descriptor.BackendKind);
    }

    [Fact]
    public void Unregister_RemovesRegisteredSession()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            Guid.NewGuid(),
            SshBackendKind.Native));
        registry.Unregister(sessionId);

        Assert.False(registry.TryGet(sessionId, out _));
    }

    [Fact]
    public void SetRuntimePassword_StoresAndReturnsPassword_ForSession()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            Guid.NewGuid(),
            SshBackendKind.Native));
        registry.SetRuntimePassword(sessionId, "session-secret");

        Assert.True(registry.TryGetRuntimePassword(sessionId, out string? password));
        Assert.Equal("session-secret", password);
    }

    [Fact]
    public void Unregister_RemovesRuntimePassword_ForSession()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            Guid.NewGuid(),
            SshBackendKind.Native));
        registry.SetRuntimePassword(sessionId, "session-secret");
        registry.Unregister(sessionId);

        Assert.False(registry.TryGetRuntimePassword(sessionId, out _));
    }
}
