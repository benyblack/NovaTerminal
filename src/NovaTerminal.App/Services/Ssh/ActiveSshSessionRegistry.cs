using System;
using System.Collections.Concurrent;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Services.Ssh;

public sealed class ActiveSshSessionRegistry
{
    private static readonly Lazy<ActiveSshSessionRegistry> Shared = new(() => new ActiveSshSessionRegistry());
    private readonly ConcurrentDictionary<Guid, ActiveSshSessionDescriptor> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _runtimePasswords = new();

    public static ActiveSshSessionRegistry Instance => Shared.Value;

    public void Register(ActiveSshSessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _sessions[descriptor.SessionId] = descriptor;
    }

    public bool TryGet(Guid sessionId, out ActiveSshSessionDescriptor? descriptor)
    {
        bool found = _sessions.TryGetValue(sessionId, out ActiveSshSessionDescriptor stored);
        descriptor = found ? stored : null;
        return found;
    }

    public void Unregister(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _runtimePasswords.TryRemove(sessionId, out _);
    }

    public void SetRuntimePassword(Guid sessionId, string? password)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            _runtimePasswords.TryRemove(sessionId, out _);
            return;
        }

        _runtimePasswords[sessionId] = password;
    }

    public bool TryGetRuntimePassword(Guid sessionId, out string? password)
    {
        bool found = _runtimePasswords.TryGetValue(sessionId, out string? stored);
        password = found ? stored : null;
        return found;
    }
}

public sealed class ActiveSshSessionDescriptor
{
    public ActiveSshSessionDescriptor(Guid sessionId, Guid profileId, SshBackendKind backendKind)
    {
        SessionId = sessionId;
        ProfileId = profileId;
        BackendKind = backendKind;
    }

    public Guid SessionId { get; }
    public Guid ProfileId { get; }
    public SshBackendKind BackendKind { get; }
}
