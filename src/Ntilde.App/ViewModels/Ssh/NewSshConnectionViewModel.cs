using NovaTerminal.Shell;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Native;

namespace NovaTerminal.ViewModels.Ssh;

public enum NewSshAuthMode
{
    Agent = 0,
    IdentityFile = 1
}

public sealed class NewSshConnectionViewModel : INotifyPropertyChanged
{
    private Guid? _profileId;
    private string _name = string.Empty;
    private string _hostName = string.Empty;
    private string _userName = string.Empty;
    private int _port = 22;
    private string _accentColor = string.Empty;
    private string _group = "General";
    private string _tagsText = string.Empty;
    private bool _isFavorite;
    private string _notes = string.Empty;
    private NewSshAuthMode _authMode = NewSshAuthMode.Agent;
    private string _identityFilePath = string.Empty;
    private string _validationError = string.Empty;
    private string _validationWarning = string.Empty;
    private SshBackendKind? _backendKind;
    private bool _rememberPasswordInVault;
    private bool _allowAgentAccess;
    private int _keepAliveIntervalSeconds = 30;
    private int _keepAliveCountMax = 3;
    private bool _enableMux;
    private int _controlPersistSeconds = 90;
    private string _extraSshArgs = string.Empty;
    private bool _connectAfterSave;
    private bool _experimentalNativeSshEnabled;
    private RemoteShellKind _remoteShellKind = RemoteShellKind.Auto;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NewSshConnectionViewModel()
    {
        JumpHops = new ObservableCollection<SshJumpHop>();
        Forwards = new ObservableCollection<PortForward>();

        // BackendWarning now reads these collections, so it has to be re-evaluated when they change —
        // adding a remote forward to a native profile should surface the problem right away, not at
        // save. Property setters raise it themselves; collection mutation has no setter to hook.
        JumpHops.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BackendWarning));
        Forwards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BackendWarning));
    }

    public Guid? ProfileId
    {
        get => _profileId;
        set => SetField(ref _profileId, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string HostName
    {
        get => _hostName;
        set => SetField(ref _hostName, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetField(ref _userName, value);
    }

    public int Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    public string AccentColor
    {
        get => _accentColor;
        set => SetField(ref _accentColor, value);
    }

    public string Group
    {
        get => _group;
        set => SetField(ref _group, value);
    }

    public string TagsText
    {
        get => _tagsText;
        set => SetField(ref _tagsText, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetField(ref _notes, value);
    }

    public NewSshAuthMode AuthMode
    {
        get => _authMode;
        set
        {
            if (SetField(ref _authMode, value))
            {
                OnPropertyChanged(nameof(IsIdentityFileAuth));
            }
        }
    }

    public bool IsIdentityFileAuth => AuthMode == NewSshAuthMode.IdentityFile;

    public string IdentityFilePath
    {
        get => _identityFilePath;
        set => SetField(ref _identityFilePath, value);
    }

    public string ValidationError
    {
        get => _validationError;
        set => SetField(ref _validationError, value);
    }

    public string ValidationWarning
    {
        get => _validationWarning;
        set => SetField(ref _validationWarning, value);
    }

    public ObservableCollection<SshJumpHop> JumpHops { get; }
    public ObservableCollection<PortForward> Forwards { get; }

    public SshBackendKind? BackendKind
    {
        get => _backendKind;
        set
        {
            if (SetField(ref _backendKind, value))
            {
                OnPropertyChanged(nameof(BackendWarning));
            }
        }
    }

    public bool RememberPasswordInVault
    {
        get => _rememberPasswordInVault;
        set => SetField(ref _rememberPasswordInVault, value);
    }

    public bool IsRememberPasswordVisible => BackendKind == SshBackendKind.Native;

    /// <summary>
    /// A3: allowlists this SSH profile for the agent-host act surface (letting
    /// agents type into / spawn it, when the global "Agent access (act)" toggle
    /// is on). Default false.
    /// </summary>
    public bool AllowAgentAccess
    {
        get => _allowAgentAccess;
        set => SetField(ref _allowAgentAccess, value);
    }

    public int KeepAliveIntervalSeconds
    {
        get => _keepAliveIntervalSeconds;
        set => SetField(ref _keepAliveIntervalSeconds, value);
    }

    public int KeepAliveCountMax
    {
        get => _keepAliveCountMax;
        set => SetField(ref _keepAliveCountMax, value);
    }

    public bool EnableMux
    {
        get => _enableMux;
        set
        {
            if (SetField(ref _enableMux, value))
            {
                // BackendWarning names mux as OpenSSH-only on native profiles; toggling it must
                // surface (or clear) that notice immediately, not at save.
                OnPropertyChanged(nameof(BackendWarning));
            }
        }
    }

    public int ControlPersistSeconds
    {
        get => _controlPersistSeconds;
        set => SetField(ref _controlPersistSeconds, value);
    }

    public string ExtraSshArgs
    {
        get => _extraSshArgs;
        set
        {
            if (SetField(ref _extraSshArgs, value))
            {
                // Same as EnableMux: the native backend cannot pass CLI arguments to anything.
                OnPropertyChanged(nameof(BackendWarning));
            }
        }
    }

    public bool ConnectAfterSave
    {
        get => _connectAfterSave;
        set => SetField(ref _connectAfterSave, value);
    }

    public bool ExperimentalNativeSshEnabled
    {
        get => _experimentalNativeSshEnabled;
        set
        {
            if (SetField(ref _experimentalNativeSshEnabled, value))
            {
                OnPropertyChanged(nameof(BackendWarning));
            }
        }
    }

    public RemoteShellKind RemoteShellKind
    {
        get => _remoteShellKind;
        set => SetField(ref _remoteShellKind, value);
    }

    public string BackendWarning
    {
        get
        {
            if (BackendKind != SshBackendKind.Native)
            {
                return string.Empty;
            }

            // Capability outranks the global toggle on purpose: if native could not serve this shape
            // even with the toggle on, "turn it on under Settings > SSH" sends the user down a
            // dead end. Name the real blocker first.
            NativeSshCapabilityResult capability = NativeSshCapability.Evaluate(Forwards, JumpHops);
            if (!capability.IsSupported)
            {
                return capability.Explanation;
            }

            // Warnings, not refusals, from here down: each of these is a valid profile that will
            // connect. They compose — a disabled toggle and ignored settings are both worth
            // knowing about — with the blocking one named first.
            var warnings = new List<string>();
            if (!ExperimentalNativeSshEnabled)
            {
                warnings.Add("Native SSH is disabled globally. Turn it on under Settings > SSH, or switch this profile back to OpenSSH.");
            }

            // Deliberately a warning rather than a save-time refusal: these settings stay stored
            // so switching the profile back to OpenSSH restores them intact — but connecting
            // natively must not look like they apply when they cannot.
            bool ignoresMux = EnableMux;
            bool ignoresExtraArgs = !string.IsNullOrWhiteSpace(ExtraSshArgs);
            if (ignoresMux && ignoresExtraArgs)
            {
                warnings.Add("Multiplexing (ControlMaster) and extra SSH arguments drive the OpenSSH client; the native backend ignores both.");
            }
            else if (ignoresMux)
            {
                warnings.Add("Multiplexing (ControlMaster) is an OpenSSH client feature; the native backend ignores it.");
            }
            else if (ignoresExtraArgs)
            {
                warnings.Add("Extra SSH arguments drive the OpenSSH client; the native backend ignores them.");
            }

            return string.Join(" ", warnings);
        }
    }

    public bool Validate()
    {
        ValidationWarning = string.Empty;

        string host = HostName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            ValidationError = "Host name is required.";
            return false;
        }

        if (Port <= 0 || Port > 65535)
        {
            ValidationError = "Port must be between 1 and 65535.";
            return false;
        }

        if (IsIdentityFileAuth && string.IsNullOrWhiteSpace(IdentityFilePath))
        {
            ValidationError = "Identity file is required when using IdentityFile auth.";
            return false;
        }

        if (KeepAliveIntervalSeconds <= 0)
        {
            ValidationError = "Keepalive interval must be greater than zero.";
            return false;
        }

        if (KeepAliveCountMax <= 0)
        {
            ValidationError = "Keepalive count max must be greater than zero.";
            return false;
        }

        if (ControlPersistSeconds < 0)
        {
            ValidationError = "ControlPersist seconds cannot be negative.";
            return false;
        }

        // A native profile whose shape the native backend cannot serve is a profile that can never
        // connect — it used to save fine and then fail at connect time with a NotSupportedException
        // banner. Refuse the save instead, and say which of the two fixes applies. Note this is
        // independent of ExperimentalNativeSshEnabled: saving a native profile while the toggle is off
        // stays allowed (the toggle is reversible; the shape is not).
        if (BackendKind == SshBackendKind.Native)
        {
            NativeSshCapabilityResult capability = NativeSshCapability.Evaluate(Forwards, JumpHops);
            if (!capability.IsSupported)
            {
                ValidationError = capability.Explanation;
                return false;
            }
        }

        if (IsIdentityFileAuth && !string.IsNullOrWhiteSpace(IdentityFilePath))
        {
            string trimmedPath = IdentityFilePath.Trim();
            if (!File.Exists(trimmedPath))
            {
                ValidationWarning = $"Identity file '{trimmedPath}' does not exist.";
            }
        }

        ValidationError = string.Empty;
        return true;
    }

    public SshProfile ToSshProfile()
    {
        Guid id = ProfileId ?? Guid.NewGuid();
        string host = HostName?.Trim() ?? string.Empty;
        string name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim();
        int port = Port > 0 ? Port : 22;
        string user = UserName?.Trim() ?? string.Empty;
        string identityPath = IsIdentityFileAuth ? IdentityFilePath?.Trim() ?? string.Empty : string.Empty;
        int keepAliveInterval = KeepAliveIntervalSeconds > 0 ? KeepAliveIntervalSeconds : 30;
        int keepAliveCountMax = KeepAliveCountMax > 0 ? KeepAliveCountMax : 3;
        int controlPersistSeconds = ControlPersistSeconds >= 0 ? ControlPersistSeconds : 90;
        return new SshProfile
        {
            Id = id,
            // Unset means "the caller never primed a backend" (the dialog primes it from the
            // global toggle before showing). The fallback follows the same rule, so a VM used
            // without the priming can never default to a backend the toggle would refuse.
            BackendKind = BackendKind ?? (ExperimentalNativeSshEnabled ? SshBackendKind.Native : SshBackendKind.OpenSsh),
            Name = name,
            GroupPath = NormalizeGroup(Group),
            Notes = Notes?.Trim() ?? string.Empty,
            AccentColor = AccentColor?.Trim() ?? string.Empty,
            Tags = BuildTags(TagsText, IsFavorite),
            Host = host,
            User = user,
            Port = port,
            AuthMode = IsIdentityFileAuth ? SshAuthMode.IdentityFile : SshAuthMode.Agent,
            IdentityFilePath = identityPath,
            JumpHops = JumpHops.Select(h => new SshJumpHop
            {
                Host = h.Host?.Trim() ?? string.Empty,
                User = h.User?.Trim() ?? string.Empty,
                Port = h.Port > 0 ? h.Port : 22
            }).ToList(),
            Forwards = Forwards.Select(f => new PortForward
            {
                Kind = f.Kind,
                BindAddress = f.BindAddress?.Trim() ?? string.Empty,
                SourcePort = f.SourcePort,
                DestinationHost = f.DestinationHost?.Trim() ?? string.Empty,
                DestinationPort = f.DestinationPort
            }).ToList(),
            MuxOptions = new SshMuxOptions
            {
                Enabled = EnableMux,
                ControlMasterAuto = true,
                ControlPersistSeconds = EnableMux ? controlPersistSeconds : 0
            },
            ServerAliveIntervalSeconds = keepAliveInterval,
            ServerAliveCountMax = keepAliveCountMax,
            ExtraSshArgs = ExtraSshArgs?.Trim() ?? string.Empty,
            RemoteShellKind = RemoteShellKind,
            AllowAgentAccess = AllowAgentAccess
        };
    }

    public static NewSshConnectionViewModel FromTerminalProfile(TerminalProfile? profile)
    {
        if (profile == null)
        {
            return new NewSshConnectionViewModel();
        }

        bool hasIdentityFile = !string.IsNullOrWhiteSpace(profile.IdentityFilePath) ||
                               !string.IsNullOrWhiteSpace(profile.SshKeyPath);

        var vm = new NewSshConnectionViewModel
        {
            ProfileId = profile.Id,
            Name = profile.Name,
            HostName = profile.SshHost,
            UserName = profile.SshUser,
            Port = profile.SshPort > 0 ? profile.SshPort : 22,
            AccentColor = profile.AccentColor ?? string.Empty,
            Group = NormalizeGroup(profile.Group),
            TagsText = FormatTags(profile.Tags),
            IsFavorite = profile.Tags.Any(tag => string.Equals(tag, "favorite", StringComparison.OrdinalIgnoreCase)),
            Notes = profile.Notes ?? string.Empty,
            BackendKind = profile.SshBackendKind,
            RemoteShellKind = profile.RemoteShellKind,
            AuthMode = hasIdentityFile ? NewSshAuthMode.IdentityFile : NewSshAuthMode.Agent,
            IdentityFilePath = !string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? profile.IdentityFilePath!
                : profile.SshKeyPath
        };

        if (profile.Forwards != null)
        {
            foreach (ForwardingRule legacy in profile.Forwards)
            {
                PortForward? forward = ConvertLegacyForward(legacy);
                if (forward != null)
                {
                    vm.Forwards.Add(forward);
                }
            }
        }

        return vm;
    }

    public void ApplySshProfile(SshProfile sshProfile)
    {
        ArgumentNullException.ThrowIfNull(sshProfile);

        ProfileId = sshProfile.Id;
        BackendKind = sshProfile.BackendKind;
        Name = sshProfile.Name;
        HostName = sshProfile.Host;
        UserName = sshProfile.User;
        Port = sshProfile.Port > 0 ? sshProfile.Port : 22;
        Group = NormalizeGroup(sshProfile.GroupPath);
        TagsText = FormatTags(sshProfile.Tags);
        Notes = sshProfile.Notes ?? string.Empty;
        AccentColor = sshProfile.AccentColor ?? string.Empty;
        IsFavorite = sshProfile.Tags.Any(tag => string.Equals(tag, "favorite", StringComparison.OrdinalIgnoreCase));
        AuthMode = sshProfile.AuthMode == SshAuthMode.IdentityFile ? NewSshAuthMode.IdentityFile : NewSshAuthMode.Agent;
        IdentityFilePath = sshProfile.IdentityFilePath ?? string.Empty;
        AllowAgentAccess = sshProfile.AllowAgentAccess;

        JumpHops.Clear();
        foreach (SshJumpHop hop in sshProfile.JumpHops)
        {
            JumpHops.Add(new SshJumpHop
            {
                Host = hop.Host,
                User = hop.User,
                Port = hop.Port > 0 ? hop.Port : 22
            });
        }

        Forwards.Clear();
        foreach (PortForward forward in sshProfile.Forwards)
        {
            Forwards.Add(new PortForward
            {
                Kind = forward.Kind,
                BindAddress = forward.BindAddress,
                SourcePort = forward.SourcePort,
                DestinationHost = forward.DestinationHost,
                DestinationPort = forward.DestinationPort
            });
        }

        KeepAliveIntervalSeconds = sshProfile.ServerAliveIntervalSeconds > 0 ? sshProfile.ServerAliveIntervalSeconds : 30;
        KeepAliveCountMax = sshProfile.ServerAliveCountMax > 0 ? sshProfile.ServerAliveCountMax : 3;
        EnableMux = sshProfile.MuxOptions.Enabled;
        ControlPersistSeconds = sshProfile.MuxOptions.ControlPersistSeconds >= 0 ? sshProfile.MuxOptions.ControlPersistSeconds : 90;
        ExtraSshArgs = sshProfile.ExtraSshArgs ?? string.Empty;
        RemoteShellKind = sshProfile.RemoteShellKind;
    }

    private static PortForward? ConvertLegacyForward(ForwardingRule legacy)
    {
        if (legacy == null || string.IsNullOrWhiteSpace(legacy.LocalAddress))
        {
            return null;
        }

        if (!TryParseEndpoint(legacy.LocalAddress, out string bindAddress, out int sourcePort))
        {
            return null;
        }

        switch (legacy.Type)
        {
            case ForwardingType.Local:
                if (!TryParseDestination(legacy.RemoteAddress, out string localDestHost, out int localDestPort))
                {
                    return null;
                }

                return new PortForward
                {
                    Kind = PortForwardKind.Local,
                    BindAddress = bindAddress,
                    SourcePort = sourcePort,
                    DestinationHost = localDestHost,
                    DestinationPort = localDestPort
                };

            case ForwardingType.Remote:
                if (!TryParseDestination(legacy.RemoteAddress, out string remoteDestHost, out int remoteDestPort))
                {
                    return null;
                }

                return new PortForward
                {
                    Kind = PortForwardKind.Remote,
                    BindAddress = bindAddress,
                    SourcePort = sourcePort,
                    DestinationHost = remoteDestHost,
                    DestinationPort = remoteDestPort
                };

            case ForwardingType.Dynamic:
                return new PortForward
                {
                    Kind = PortForwardKind.Dynamic,
                    BindAddress = bindAddress,
                    SourcePort = sourcePort
                };

            default:
                return null;
        }
    }

    private static bool TryParseEndpoint(string value, out string bindAddress, out int port)
    {
        bindAddress = string.Empty;
        port = 0;

        string trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        int colon = trimmed.LastIndexOf(':');
        if (colon <= 0)
        {
            return int.TryParse(trimmed, out port);
        }

        bindAddress = trimmed[..colon].Trim();
        return int.TryParse(trimmed[(colon + 1)..].Trim(), out port);
    }

    private static bool TryParseDestination(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        string trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        int colon = trimmed.LastIndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        host = trimmed[..colon].Trim();
        return !string.IsNullOrWhiteSpace(host) && int.TryParse(trimmed[(colon + 1)..].Trim(), out port);
    }

    private static string NormalizeGroup(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed) ? "General" : trimmed;
    }

    private static string FormatTags(IEnumerable<string>? tags)
    {
        return string.Join(", ", NormalizeGeneralTags(tags));
    }

    private static List<string> BuildTags(string? tagsText, bool isFavorite)
    {
        List<string> tags = NormalizeGeneralTags((tagsText ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (isFavorite)
        {
            tags.Insert(0, "favorite");
        }

        return tags;
    }

    private static List<string> NormalizeGeneralTags(IEnumerable<string>? tags)
    {
        return (tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => !string.Equals(tag, "favorite", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

