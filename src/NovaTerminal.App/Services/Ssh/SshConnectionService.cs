using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.OpenSsh;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Services.Ssh;

public sealed class SshLaunchDetails
{
    public required string SshPath { get; init; }
    public required string ConfigPath { get; init; }
    public required string Alias { get; init; }
    public required string CommandLine { get; init; }
}

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

    public SshLaunchDetails BuildLaunchDetails(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SshProfile sshProfile = EnsureStoreProfile(profile);

        var planner = new SshLaunchPlanner(_profileStore, new OpenSshConfigCompiler());
        SshLaunchPlan plan = planner.Plan(sshProfile.Id, diagnosticsLevel.ToArguments());
        string argsText = SshArgBuilder.BuildCommandLine(plan.Arguments);
        string commandText = $"{QuoteToken(plan.SshExecutablePath)} {argsText}";

        return new SshLaunchDetails
        {
            SshPath = plan.SshExecutablePath,
            ConfigPath = plan.ConfigFilePath,
            Alias = plan.Alias,
            CommandLine = commandText
        };
    }

    public string BuildLaunchCommand(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
    {
        return BuildLaunchDetails(profile, diagnosticsLevel).CommandLine;
    }

    private SshProfile EnsureStoreProfile(TerminalProfile profile)
    {
        SshProfile? existing = _profileStore.GetProfile(profile.Id);
        if (existing != null)
        {
            return existing;
        }

        var converted = new SshProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Host = profile.SshHost?.Trim() ?? string.Empty,
            User = profile.SshUser?.Trim() ?? string.Empty,
            Port = profile.SshPort > 0 ? profile.SshPort : 22,
            AuthMode = !profile.UseSshAgent && (!string.IsNullOrWhiteSpace(profile.IdentityFilePath) || !string.IsNullOrWhiteSpace(profile.SshKeyPath))
                ? SshAuthMode.IdentityFile
                : SshAuthMode.Agent,
            IdentityFilePath = !string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? profile.IdentityFilePath!.Trim()
                : profile.SshKeyPath?.Trim() ?? string.Empty
        };

        _profileStore.SaveProfile(converted);
        return converted;
    }

    private static string QuoteToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "\"\"";
        }

        return token.Any(char.IsWhiteSpace)
            ? $"\"{token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : token;
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
