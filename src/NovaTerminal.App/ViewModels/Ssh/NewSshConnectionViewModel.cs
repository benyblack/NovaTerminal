using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;

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
    private bool _isFavorite;
    private string _notes = string.Empty;
    private NewSshAuthMode _authMode = NewSshAuthMode.Agent;
    private string _identityFilePath = string.Empty;
    private string _validationError = string.Empty;
    private string _validationWarning = string.Empty;
    private SshBackendKind? _backendKind;
    private bool _rememberPasswordInVault;
    private int _keepAliveIntervalSeconds = 30;
    private int _keepAliveCountMax = 3;
    private bool _enableMux;
    private int _controlPersistSeconds = 90;
    private string _extraSshArgs = string.Empty;
    private bool _connectAfterSave;
    private bool _experimentalNativeSshEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NewSshConnectionViewModel()
    {
        JumpHops = new ObservableCollection<SshJumpHop>();
        Forwards = new ObservableCollection<PortForward>();
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
        set => SetField(ref _enableMux, value);
    }

    public int ControlPersistSeconds
    {
        get => _controlPersistSeconds;
        set => SetField(ref _controlPersistSeconds, value);
    }

    public string ExtraSshArgs
    {
        get => _extraSshArgs;
        set => SetField(ref _extraSshArgs, value);
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

    public string BackendWarning =>
        BackendKind == SshBackendKind.Native && !ExperimentalNativeSshEnabled
            ? "Native SSH is disabled globally. Enable ExperimentalNativeSshEnabled in settings or switch this profile back to OpenSSH."
            : string.Empty;

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
            BackendKind = BackendKind ?? SshBackendKind.OpenSsh,
            Name = name,
            Notes = Notes?.Trim() ?? string.Empty,
            AccentColor = AccentColor?.Trim() ?? string.Empty,
            Tags = BuildTags(IsFavorite),
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
            ExtraSshArgs = ExtraSshArgs?.Trim() ?? string.Empty
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
            IsFavorite = profile.Tags.Any(tag => string.Equals(tag, "favorite", StringComparison.OrdinalIgnoreCase)),
            Notes = profile.Notes ?? string.Empty,
            BackendKind = profile.SshBackendKind,
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
        Notes = sshProfile.Notes ?? string.Empty;
        AccentColor = sshProfile.AccentColor ?? string.Empty;
        IsFavorite = sshProfile.Tags.Any(tag => string.Equals(tag, "favorite", StringComparison.OrdinalIgnoreCase));
        AuthMode = sshProfile.AuthMode == SshAuthMode.IdentityFile ? NewSshAuthMode.IdentityFile : NewSshAuthMode.Agent;
        IdentityFilePath = sshProfile.IdentityFilePath ?? string.Empty;

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

    private static List<string> BuildTags(bool isFavorite)
    {
        if (!isFavorite)
        {
            return new List<string>();
        }

        return new List<string> { "favorite" };
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

