using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Ssh.Sessions;

public sealed class SshSessionFactory : ISshSessionFactory
{
    private readonly ISshProfileStore _profileStore;
    private readonly ISshProcessLauncher? _launcher;
    private readonly INativeSshInterop? _nativeInterop;
    private readonly ISshInteractionHandler? _nativeInteractionHandler;
    private readonly bool _nativeSshEnabled;

    public SshSessionFactory(ISshProfileStore profileStore)
        : this(profileStore, null, null)
    {
    }

    public SshSessionFactory(
        ISshProfileStore profileStore,
        ISshProcessLauncher? launcher = null,
        INativeSshInterop? nativeInterop = null,
        ISshInteractionHandler? nativeInteractionHandler = null,
        bool nativeSshEnabled = true)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _launcher = launcher;
        _nativeInterop = nativeInterop;
        _nativeInteractionHandler = nativeInteractionHandler;
        _nativeSshEnabled = nativeSshEnabled;
    }

    public SshSessionFactory(
        ISshProcessLauncher? launcher = null,
        INativeSshInterop? nativeInterop = null,
        ISshInteractionHandler? nativeInteractionHandler = null,
        bool nativeSshEnabled = true)
        : this(new JsonSshProfileStore(), launcher, nativeInterop, nativeInteractionHandler, nativeSshEnabled)
    {
    }

    public ITerminalSession Create(
        Guid profileId,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        Action<string>? log = null)
    {
        SshProfile profile = _profileStore.GetProfile(profileId)
            ?? throw new InvalidOperationException($"SSH profile '{profileId}' was not found.");

        return profile.BackendKind switch
        {
            SshBackendKind.Native => CreateNativeSession(profile, cols, rows, diagnosticsLevel, extraArgs, log),
            _ => new OpenSshSession(profile.Id, _profileStore, cols, rows, diagnosticsLevel, extraArgs, _launcher, log)
        };
    }

    private NativeSshSession CreateNativeSession(
        SshProfile profile,
        int cols,
        int rows,
        SshDiagnosticsLevel diagnosticsLevel,
        IReadOnlyList<string>? extraArgs,
        Action<string>? log)
    {
        if (!_nativeSshEnabled)
        {
            throw new InvalidOperationException(
                "Native SSH is disabled globally. Switch this profile back to OpenSSH or enable ExperimentalNativeSshEnabled.");
        }

        string path = profile.JumpHops.Count switch
        {
            0 => "direct",
            1 => "jump-host",
            _ => "jump-host-unsupported"
        };

        log?.Invoke($"[SshSessionFactory] backend=native path={path} profile={profile.Name}");
        return new NativeSshSession(profile, cols, rows, diagnosticsLevel, extraArgs, log, _nativeInterop, _nativeInteractionHandler);
    }
}
