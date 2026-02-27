using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private NewSshAuthMode _authMode = NewSshAuthMode.Agent;
    private string _identityFilePath = string.Empty;
    private string _validationError = string.Empty;
    private bool _connectAfterSave;

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public bool ConnectAfterSave
    {
        get => _connectAfterSave;
        set => SetField(ref _connectAfterSave, value);
    }

    public bool Validate()
    {
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

        return new SshProfile
        {
            Id = id,
            Name = name,
            Host = host,
            User = user,
            Port = port,
            AuthMode = IsIdentityFileAuth ? SshAuthMode.IdentityFile : SshAuthMode.Agent,
            IdentityFilePath = identityPath
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

        return new NewSshConnectionViewModel
        {
            ProfileId = profile.Id,
            Name = profile.Name,
            HostName = profile.SshHost,
            UserName = profile.SshUser,
            Port = profile.SshPort > 0 ? profile.SshPort : 22,
            AuthMode = hasIdentityFile ? NewSshAuthMode.IdentityFile : NewSshAuthMode.Agent,
            IdentityFilePath = !string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? profile.IdentityFilePath!
                : profile.SshKeyPath
        };
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
