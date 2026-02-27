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

    public NewSshConnectionViewModel CreateEditorViewModel(TerminalProfile? profile)
    {
        NewSshConnectionViewModel vm = NewSshConnectionViewModel.FromTerminalProfile(profile);
        if (profile == null)
        {
            return vm;
        }

        SshProfile? stored = _profileStore.GetProfile(profile.Id);
        if (stored != null)
        {
            vm.ApplySshProfile(stored);
        }
        else
        {
            vm.ApplySshProfile(MapLegacyTerminalProfile(profile));
        }

        return vm;
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
        terminalProfile.Notes = viewModel.Notes?.Trim() ?? string.Empty;
        terminalProfile.AccentColor = viewModel.AccentColor?.Trim() ?? string.Empty;

        bool useIdentityFile = sshProfile.AuthMode == SshAuthMode.IdentityFile &&
                               !string.IsNullOrWhiteSpace(sshProfile.IdentityFilePath);
        terminalProfile.UseSshAgent = !useIdentityFile;
        terminalProfile.IdentityFilePath = useIdentityFile ? sshProfile.IdentityFilePath : string.Empty;
        terminalProfile.SshKeyPath = useIdentityFile ? sshProfile.IdentityFilePath : string.Empty;

        terminalProfile.Tags ??= new List<string>();
        terminalProfile.Tags.RemoveAll(tag => string.Equals(tag, "favorite", StringComparison.OrdinalIgnoreCase));
        if (viewModel.IsFavorite)
        {
            terminalProfile.Tags.Add("favorite");
        }

        terminalProfile.Forwards = sshProfile.Forwards.Select(ConvertToLegacyForward).ToList();
        terminalProfile.JumpHostProfileId = ResolveLegacyJumpHostProfileId(sshProfile.JumpHops, terminalProfiles);

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

        SshProfile converted = MapLegacyTerminalProfile(profile);

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

    private static SshProfile MapLegacyTerminalProfile(TerminalProfile profile)
    {
        var mapped = new SshProfile
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

        if (profile.Forwards != null)
        {
            foreach (ForwardingRule legacy in profile.Forwards)
            {
                PortForward? converted = ConvertToCoreForward(legacy);
                if (converted != null)
                {
                    mapped.Forwards.Add(converted);
                }
            }
        }

        return mapped;
    }

    private static PortForward? ConvertToCoreForward(ForwardingRule legacy)
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

    private static ForwardingRule ConvertToLegacyForward(PortForward forward)
    {
        string local = string.IsNullOrWhiteSpace(forward.BindAddress)
            ? forward.SourcePort.ToString()
            : $"{forward.BindAddress}:{forward.SourcePort}";

        return forward.Kind switch
        {
            PortForwardKind.Local => new ForwardingRule
            {
                Type = ForwardingType.Local,
                LocalAddress = local,
                RemoteAddress = $"{forward.DestinationHost}:{forward.DestinationPort}"
            },
            PortForwardKind.Remote => new ForwardingRule
            {
                Type = ForwardingType.Remote,
                LocalAddress = local,
                RemoteAddress = $"{forward.DestinationHost}:{forward.DestinationPort}"
            },
            PortForwardKind.Dynamic => new ForwardingRule
            {
                Type = ForwardingType.Dynamic,
                LocalAddress = local,
                RemoteAddress = string.Empty
            },
            _ => new ForwardingRule()
        };
    }

    private static Guid? ResolveLegacyJumpHostProfileId(IEnumerable<SshJumpHop> jumpHops, IList<TerminalProfile> profiles)
    {
        SshJumpHop? firstHop = jumpHops?.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.Host));
        if (firstHop == null)
        {
            return null;
        }

        TerminalProfile? matched = profiles.FirstOrDefault(p =>
            p.Type == ConnectionType.SSH &&
            string.Equals(p.SshHost?.Trim(), firstHop.Host?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.SshUser?.Trim(), firstHop.User?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (p.SshPort > 0 ? p.SshPort : 22) == (firstHop.Port > 0 ? firstHop.Port : 22));

        return matched?.Id;
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
}
