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
    private const string FavoriteTag = "favorite";

    private readonly ISshProfileStore _profileStore;

    public SshConnectionService(ISshProfileStore? profileStore = null)
    {
        _profileStore = profileStore ?? new JsonSshProfileStore();
    }

    public IReadOnlyList<TerminalProfile> GetConnectionProfiles()
    {
        return _profileStore.GetProfiles()
            .Select(ToRuntimeProfile)
            .OrderByDescending(profile => profile.Tags.Any(IsFavoriteTag))
            .ThenBy(profile => profile.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Id)
            .ToArray();
    }

    public TerminalProfile? GetConnectionProfile(Guid profileId)
    {
        SshProfile? profile = _profileStore.GetProfile(profileId);
        return profile == null ? null : ToRuntimeProfile(profile);
    }

    public NewSshConnectionViewModel CreateEditorViewModel(TerminalProfile? profile)
    {
        if (profile == null)
        {
            return new NewSshConnectionViewModel();
        }

        SshProfile? stored = _profileStore.GetProfile(profile.Id);
        var vm = new NewSshConnectionViewModel();
        if (stored != null)
        {
            vm.ApplySshProfile(stored);
            return vm;
        }

        return NewSshConnectionViewModel.FromTerminalProfile(profile);
    }

    public SshProfile SaveProfile(NewSshConnectionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (!viewModel.Validate())
        {
            throw new InvalidOperationException(viewModel.ValidationError);
        }

        SshProfile incoming = viewModel.ToSshProfile();
        SshProfile? existing = _profileStore.GetProfile(incoming.Id);
        if (existing != null && viewModel.BackendKind is null)
        {
            incoming.BackendKind = existing.BackendKind;
        }

        SshProfile merged = MergeProfile(existing, incoming, viewModel.IsFavorite);

        _profileStore.SaveProfile(merged);
        return _profileStore.GetProfile(merged.Id) ?? merged;
    }

    // Legacy overload retained for compatibility while settings/profile separation lands.
    public TerminalProfile SaveProfile(NewSshConnectionViewModel viewModel, IList<TerminalProfile> terminalProfiles)
    {
        _ = terminalProfiles;
        return ToRuntimeProfile(SaveProfile(viewModel));
    }

    public void SaveConnectionProfile(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        SshProfile? existing = _profileStore.GetProfile(profile.Id);
        SshProfile candidate = existing is null
            ? MapLegacyTerminalProfile(profile)
            : CloneProfile(existing);

        candidate.Id = profile.Id;
        candidate.Name = profile.Name?.Trim() ?? string.Empty;
        candidate.Host = profile.SshHost?.Trim() ?? string.Empty;
        candidate.User = profile.SshUser?.Trim() ?? string.Empty;
        candidate.Port = profile.SshPort > 0 ? profile.SshPort : 22;
        candidate.Notes = profile.Notes?.Trim() ?? string.Empty;
        candidate.AccentColor = profile.AccentColor?.Trim() ?? string.Empty;
        candidate.GroupPath = profile.Group?.Trim() ?? candidate.GroupPath;
        candidate.BackendKind = profile.SshBackendKind;

        bool favorite = profile.Tags?.Any(IsFavoriteTag) == true;
        candidate.Tags = NormalizeTags(profile.Tags, favorite);

        string identityPath = !string.IsNullOrWhiteSpace(profile.IdentityFilePath)
            ? profile.IdentityFilePath!.Trim()
            : profile.SshKeyPath?.Trim() ?? string.Empty;
        bool useIdentity = !profile.UseSshAgent && !string.IsNullOrWhiteSpace(identityPath);
        candidate.AuthMode = useIdentity ? SshAuthMode.IdentityFile : SshAuthMode.Agent;
        candidate.IdentityFilePath = useIdentity ? identityPath : string.Empty;

        _profileStore.SaveProfile(candidate);
    }

    public void SaveConnectionProfiles(IEnumerable<TerminalProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (TerminalProfile profile in profiles)
        {
            SaveConnectionProfile(profile);
        }
    }

    public bool DeleteProfile(Guid profileId)
    {
        return _profileStore.DeleteProfile(profileId);
    }

    public int MergeImportedProfiles(IEnumerable<TerminalProfile> importedLegacyProfiles)
    {
        ArgumentNullException.ThrowIfNull(importedLegacyProfiles);

        int changedCount = 0;
        Dictionary<string, SshProfile> byName = _profileStore.GetProfiles()
            .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        List<TerminalProfile> legacyProfiles = importedLegacyProfiles
            .Where(profile => profile.Type == ConnectionType.SSH)
            .ToList();

        foreach (TerminalProfile imported in legacyProfiles)
        {
            SshProfile importedProfile = MapLegacyTerminalProfile(imported, legacyProfiles);
            if (string.IsNullOrWhiteSpace(importedProfile.Host))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(importedProfile.Name))
            {
                importedProfile.Name = importedProfile.Host;
            }

            if (byName.TryGetValue(importedProfile.Name, out SshProfile? existing))
            {
                SshProfile merged = CloneProfile(existing);
                bool differs = !string.Equals(merged.Host, importedProfile.Host, StringComparison.Ordinal) ||
                               !string.Equals(merged.User, importedProfile.User, StringComparison.Ordinal) ||
                               merged.Port != importedProfile.Port ||
                               merged.AuthMode != importedProfile.AuthMode ||
                               !string.Equals(merged.IdentityFilePath, importedProfile.IdentityFilePath, StringComparison.Ordinal);

                if (!differs)
                {
                    continue;
                }

                merged.Host = importedProfile.Host;
                merged.User = importedProfile.User;
                merged.Port = importedProfile.Port;
                merged.AuthMode = importedProfile.AuthMode;
                merged.IdentityFilePath = importedProfile.IdentityFilePath;
                _profileStore.SaveProfile(merged);
                changedCount++;
                continue;
            }

            _profileStore.SaveProfile(importedProfile);
            byName[importedProfile.Name] = importedProfile;
            changedCount++;
        }

        return changedCount;
    }

    public SshLaunchDetails BuildLaunchDetails(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return BuildLaunchDetails(profile.Id, diagnosticsLevel, profile);
    }

    public SshLaunchDetails BuildLaunchDetails(Guid profileId, SshDiagnosticsLevel diagnosticsLevel)
    {
        return BuildLaunchDetails(profileId, diagnosticsLevel, null);
    }

    public string BuildLaunchCommand(TerminalProfile profile, SshDiagnosticsLevel diagnosticsLevel)
    {
        return BuildLaunchDetails(profile, diagnosticsLevel).CommandLine;
    }

    public string BuildLaunchCommand(Guid profileId, SshDiagnosticsLevel diagnosticsLevel)
    {
        return BuildLaunchDetails(profileId, diagnosticsLevel).CommandLine;
    }

    private SshLaunchDetails BuildLaunchDetails(Guid profileId, SshDiagnosticsLevel diagnosticsLevel, TerminalProfile? fallbackProfile)
    {
        SshProfile sshProfile = EnsureStoreProfile(profileId, fallbackProfile);

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

    private SshProfile EnsureStoreProfile(Guid profileId, TerminalProfile? fallbackProfile)
    {
        SshProfile? existing = _profileStore.GetProfile(profileId);
        if (existing != null)
        {
            return existing;
        }

        if (fallbackProfile == null)
        {
            throw new InvalidOperationException($"SSH profile '{profileId}' was not found.");
        }

        SshProfile converted = MapLegacyTerminalProfile(fallbackProfile);
        _profileStore.SaveProfile(converted);
        return converted;
    }

    private static SshProfile MergeProfile(SshProfile? existing, SshProfile incoming, bool favorite)
    {
        SshProfile merged = existing is null
            ? CloneProfile(incoming)
            : CloneProfile(existing);

        merged.Id = incoming.Id;
        merged.BackendKind = incoming.BackendKind;
        merged.Name = incoming.Name;
        merged.Host = incoming.Host;
        merged.User = incoming.User;
        merged.Port = incoming.Port;
        merged.AuthMode = incoming.AuthMode;
        merged.IdentityFilePath = incoming.IdentityFilePath;
        merged.JumpHops = incoming.JumpHops.Select(CloneJumpHop).ToList();
        merged.Forwards = incoming.Forwards.Select(CloneForward).ToList();
        merged.MuxOptions = CloneMuxOptions(incoming.MuxOptions);
        merged.ServerAliveIntervalSeconds = incoming.ServerAliveIntervalSeconds;
        merged.ServerAliveCountMax = incoming.ServerAliveCountMax;
        merged.ExtraSshArgs = incoming.ExtraSshArgs;
        merged.WorkingDirectory = incoming.WorkingDirectory;
        merged.Notes = incoming.Notes;
        merged.AccentColor = incoming.AccentColor;

        // Group isn't editable in the minimal dialog yet, so preserve existing if present.
        if (string.IsNullOrWhiteSpace(merged.GroupPath))
        {
            merged.GroupPath = "General";
        }

        IEnumerable<string> sourceTags = (IEnumerable<string>?)existing?.Tags ?? Array.Empty<string>();
        merged.Tags = NormalizeTags(sourceTags, favorite);
        return merged;
    }

    public static TerminalProfile ToRuntimeProfile(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        bool useIdentityFile = profile.AuthMode == SshAuthMode.IdentityFile &&
                               !string.IsNullOrWhiteSpace(profile.IdentityFilePath);

        return new TerminalProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Type = ConnectionType.SSH,
            Command = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh",
            SshHost = profile.Host,
            SshUser = profile.User,
            SshPort = profile.Port > 0 ? profile.Port : 22,
            SshBackendKind = profile.BackendKind,
            UseSshAgent = !useIdentityFile,
            IdentityFilePath = useIdentityFile ? profile.IdentityFilePath : string.Empty,
            SshKeyPath = useIdentityFile ? profile.IdentityFilePath : string.Empty,
            Group = string.IsNullOrWhiteSpace(profile.GroupPath) ? "General" : profile.GroupPath,
            Notes = profile.Notes ?? string.Empty,
            AccentColor = profile.AccentColor ?? string.Empty,
            Tags = (profile.Tags ?? new List<string>()).ToList(),
            Forwards = profile.Forwards.Select(ConvertToLegacyForward).ToList()
        };
    }

    public static SshProfile MapLegacyTerminalProfile(TerminalProfile profile, IReadOnlyList<TerminalProfile>? allProfiles = null)
    {
        var mapped = new SshProfile
        {
            Id = profile.Id,
            BackendKind = profile.SshBackendKind,
            Name = (profile.Name ?? string.Empty).Trim(),
            Host = profile.SshHost?.Trim() ?? string.Empty,
            User = profile.SshUser?.Trim() ?? string.Empty,
            Port = profile.SshPort > 0 ? profile.SshPort : 22,
            GroupPath = profile.Group?.Trim() ?? "General",
            Notes = profile.Notes?.Trim() ?? string.Empty,
            AccentColor = profile.AccentColor?.Trim() ?? string.Empty,
            Tags = NormalizeTags(profile.Tags, profile.Tags?.Any(IsFavoriteTag) == true),
            AuthMode = !profile.UseSshAgent && (!string.IsNullOrWhiteSpace(profile.IdentityFilePath) || !string.IsNullOrWhiteSpace(profile.SshKeyPath))
                ? SshAuthMode.IdentityFile
                : SshAuthMode.Agent,
            IdentityFilePath = !string.IsNullOrWhiteSpace(profile.IdentityFilePath)
                ? profile.IdentityFilePath!.Trim()
                : profile.SshKeyPath?.Trim() ?? string.Empty,
            JumpHops = BuildJumpHopsFromLegacy(profile, allProfiles)
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

    private static List<string> NormalizeTags(IEnumerable<string>? tags, bool favorite)
    {
        List<string> list = (tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => !IsFavoriteTag(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (favorite)
        {
            list.Insert(0, FavoriteTag);
        }

        return list;
    }

    private static bool IsFavoriteTag(string? tag)
    {
        return string.Equals(tag?.Trim(), FavoriteTag, StringComparison.OrdinalIgnoreCase);
    }

    private static List<SshJumpHop> BuildJumpHopsFromLegacy(TerminalProfile profile, IReadOnlyList<TerminalProfile>? allProfiles)
    {
        if (!profile.JumpHostProfileId.HasValue || allProfiles == null || allProfiles.Count == 0)
        {
            return new List<SshJumpHop>();
        }

        var chain = new List<SshJumpHop>();
        var visited = new HashSet<Guid>();
        Guid? current = profile.JumpHostProfileId;

        while (current.HasValue && visited.Add(current.Value))
        {
            TerminalProfile? hop = allProfiles.FirstOrDefault(candidate => candidate.Id == current.Value);
            if (hop == null || string.IsNullOrWhiteSpace(hop.SshHost))
            {
                break;
            }

            chain.Add(new SshJumpHop
            {
                Host = hop.SshHost?.Trim() ?? string.Empty,
                User = hop.SshUser?.Trim() ?? string.Empty,
                Port = hop.SshPort > 0 ? hop.SshPort : 22
            });

            current = hop.JumpHostProfileId;
        }

        chain.Reverse();
        return chain;
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

    private static SshProfile CloneProfile(SshProfile profile)
    {
        return new SshProfile
        {
            Id = profile.Id,
            BackendKind = profile.BackendKind,
            Name = profile.Name,
            GroupPath = profile.GroupPath,
            Notes = profile.Notes,
            AccentColor = profile.AccentColor,
            Tags = (profile.Tags ?? new List<string>()).ToList(),
            Host = profile.Host,
            User = profile.User,
            Port = profile.Port,
            AuthMode = profile.AuthMode,
            IdentityFilePath = profile.IdentityFilePath,
            JumpHops = profile.JumpHops.Select(CloneJumpHop).ToList(),
            Forwards = profile.Forwards.Select(CloneForward).ToList(),
            MuxOptions = CloneMuxOptions(profile.MuxOptions),
            ServerAliveIntervalSeconds = profile.ServerAliveIntervalSeconds,
            ServerAliveCountMax = profile.ServerAliveCountMax,
            ExtraSshArgs = profile.ExtraSshArgs,
            WorkingDirectory = profile.WorkingDirectory
        };
    }

    private static SshJumpHop CloneJumpHop(SshJumpHop hop)
    {
        return new SshJumpHop
        {
            Host = hop.Host,
            User = hop.User,
            Port = hop.Port
        };
    }

    private static PortForward CloneForward(PortForward forward)
    {
        return new PortForward
        {
            Kind = forward.Kind,
            BindAddress = forward.BindAddress,
            SourcePort = forward.SourcePort,
            DestinationHost = forward.DestinationHost,
            DestinationPort = forward.DestinationPort
        };
    }

    private static SshMuxOptions CloneMuxOptions(SshMuxOptions? mux)
    {
        mux ??= new SshMuxOptions();
        return new SshMuxOptions
        {
            Enabled = mux.Enabled,
            ControlMasterAuto = mux.ControlMasterAuto,
            ControlPath = mux.ControlPath,
            ControlPersistSeconds = mux.ControlPersistSeconds
        };
    }
}
