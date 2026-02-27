using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Services.Ssh;

public sealed class SshConnectionService
{
    private readonly ISshProfileStore _profileStore;

    public SshConnectionService(ISshProfileStore? profileStore = null)
    {
        _profileStore = profileStore ?? new JsonSshProfileStore();
    }

    public TerminalProfile SaveProfile(NewSshConnectionViewModel viewModel, IList<TerminalProfile> terminalProfiles)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(terminalProfiles);

        if (!viewModel.Validate())
        {
            throw new InvalidOperationException(viewModel.ValidationError);
        }

        SshProfile sshProfile = viewModel.ToSshProfile();
        _profileStore.SaveProfile(sshProfile);

        TerminalProfile terminalProfile = terminalProfiles.FirstOrDefault(p => p.Id == sshProfile.Id)
            ?? CreateSshTerminalProfile(sshProfile.Id);

        terminalProfile.Name = sshProfile.Name;
        terminalProfile.Type = ConnectionType.SSH;
        terminalProfile.SshHost = sshProfile.Host;
        terminalProfile.SshUser = sshProfile.User;
        terminalProfile.SshPort = sshProfile.Port;

        bool useIdentityFile = sshProfile.AuthMode == SshAuthMode.IdentityFile &&
                               !string.IsNullOrWhiteSpace(sshProfile.IdentityFilePath);
        terminalProfile.UseSshAgent = !useIdentityFile;
        terminalProfile.IdentityFilePath = useIdentityFile ? sshProfile.IdentityFilePath : string.Empty;
        terminalProfile.SshKeyPath = useIdentityFile ? sshProfile.IdentityFilePath : string.Empty;

        if (!terminalProfiles.Any(p => p.Id == terminalProfile.Id))
        {
            terminalProfiles.Add(terminalProfile);
        }

        return terminalProfile;
    }

    private static TerminalProfile CreateSshTerminalProfile(Guid id)
    {
        return new TerminalProfile
        {
            Id = id,
            Name = "SSH",
            Type = ConnectionType.SSH,
            Command = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh",
            SshPort = 22
        };
    }
}
