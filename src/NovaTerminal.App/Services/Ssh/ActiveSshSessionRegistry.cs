using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using NovaTerminal.Platform.Ssh.Models;

namespace NovaTerminal.Services.Ssh;

public sealed class ActiveSshSessionRegistry
{
    private static readonly Lazy<ActiveSshSessionRegistry> Shared = new(() => new ActiveSshSessionRegistry());
    private readonly ConcurrentDictionary<Guid, ActiveSshSessionDescriptor> _sessions = new();

    /// <summary>
    /// Session passwords, held as UTF-8 in pinned buffers rather than as <see cref="string"/>.
    /// </summary>
    /// <remarks>
    /// #121: this used to be a <c>ConcurrentDictionary&lt;Guid, string&gt;</c>, which meant plaintext
    /// credentials sat on the managed heap for the whole lifetime of a session — hours — in a process
    /// that also renders untrusted terminal output. A <see cref="string"/> cannot be cleared, and the
    /// GC is free to copy it while compacting, so the old copy could not even be reliably
    /// *over*written.
    ///
    /// Two properties change here, and both are about the long-lived copy specifically:
    ///
    /// <list type="bullet">
    /// <item><b>Clearable.</b> Bytes are zeroed on overwrite and on <see cref="Unregister"/>, so the
    /// window shrinks from "process lifetime" to "session lifetime".</item>
    /// <item><b>Not relocatable.</b> The buffer is allocated pinned, so compaction cannot leave a stale
    /// copy elsewhere in the heap that nothing has a reference to and nothing can clear.</item>
    /// </list>
    ///
    /// What this does <em>not</em> do, stated plainly so nobody reads more into it: transient
    /// <see cref="string"/> copies still exist. <see cref="TryGetRuntimePassword"/> has to return one
    /// because every consumer needs one — <c>NativeSshConnectionOptions.Password</c> is a string and the
    /// interop marshals it as <c>LPUTF8Str</c>. Those copies are short-lived and eligible for collection
    /// immediately, which is a materially different exposure from one that persists for the session.
    /// Removing them means changing the FFI signature to take a buffer; that is tracked on #121 and is
    /// deliberately not bundled here.
    ///
    /// A plain dictionary under an explicit lock, not a <see cref="ConcurrentDictionary{TKey,TValue}"/>:
    /// zeroing a buffer that a concurrent reader is mid-decode would hand that reader a half-zeroed
    /// password and fail its auth for no visible reason. Reads, writes and clears all happen inside the
    /// lock so the bytes are never observed while being wiped. Contention is irrelevant — this is touched
    /// on auth and on teardown, not per keystroke.
    /// </remarks>
    private readonly Dictionary<Guid, byte[]> _runtimePasswords = new();
    private readonly object _runtimePasswordGate = new();

    public static ActiveSshSessionRegistry Instance => Shared.Value;

    public void Register(ActiveSshSessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _sessions[descriptor.SessionId] = descriptor;
    }

    public bool TryGet(Guid sessionId, out ActiveSshSessionDescriptor? descriptor)
    {
        bool found = _sessions.TryGetValue(sessionId, out ActiveSshSessionDescriptor? stored);
        descriptor = found ? stored : null;
        return found;
    }

    public bool TryGetActiveNativeSession(Guid profileId, Guid sessionId, out ActiveSshSessionDescriptor? descriptor)
    {
        if (!TryGet(sessionId, out descriptor) ||
            descriptor is null ||
            descriptor.ProfileId != profileId ||
            descriptor.BackendKind != SshBackendKind.Native)
        {
            descriptor = null;
            return false;
        }

        return true;
    }

    public void Unregister(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        ClearRuntimePassword(sessionId);
    }

    public void SetRuntimePassword(Guid sessionId, string? password)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ClearRuntimePassword(sessionId);
            return;
        }

        // Pinned so the GC cannot relocate it: a moved buffer leaves plaintext behind at the old
        // address with no reference to it, which is the one thing zeroing cannot fix afterwards.
        int byteCount = Encoding.UTF8.GetByteCount(password);
        byte[] buffer = GC.AllocateArray<byte>(byteCount, pinned: true);
        Encoding.UTF8.GetBytes(password, buffer);

        lock (_runtimePasswordGate)
        {
            if (_runtimePasswords.TryGetValue(sessionId, out byte[]? existing))
            {
                CryptographicOperations.ZeroMemory(existing);
            }

            _runtimePasswords[sessionId] = buffer;
        }
    }

    /// <summary>
    /// Returns the session's password, or <c>null</c> when none is held.
    /// </summary>
    /// <remarks>
    /// The returned string is a transient copy — see the note on <c>_runtimePasswords</c>. Callers should
    /// use it and let it go rather than storing it anywhere with a longer life than the operation.
    /// </remarks>
    public bool TryGetRuntimePassword(Guid sessionId, out string? password)
    {
        lock (_runtimePasswordGate)
        {
            if (!_runtimePasswords.TryGetValue(sessionId, out byte[]? stored))
            {
                password = null;
                return false;
            }

            // Decoded under the lock so a concurrent Unregister cannot zero the bytes mid-decode and
            // hand back a truncated password that would fail auth for no discoverable reason.
            password = Encoding.UTF8.GetString(stored);
            return true;
        }
    }

    private void ClearRuntimePassword(Guid sessionId)
    {
        lock (_runtimePasswordGate)
        {
            if (_runtimePasswords.Remove(sessionId, out byte[]? removed))
            {
                CryptographicOperations.ZeroMemory(removed);
            }
        }
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
