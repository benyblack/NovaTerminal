using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Services.Ssh;

/// <summary>
/// One-way migration shim while SSH connections are separated from TerminalSettings.Profiles.
/// Future native SSH storage can swap this service without touching MainWindow startup flow.
/// </summary>
public sealed class SshLegacyProfileMigrationService
{
    private readonly ISshProfileStore _profileStore;

    public SshLegacyProfileMigrationService(ISshProfileStore? profileStore = null)
    {
        _profileStore = profileStore ?? new JsonSshProfileStore();
    }

    public bool MigrateLegacyProfiles(TerminalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Profiles ??= new List<TerminalProfile>();
        List<TerminalProfile> legacySsh = settings.Profiles
            .Where(profile => profile.Type == ConnectionType.SSH)
            .ToList();

        bool changed = false;
        if (legacySsh.Count > 0)
        {
            IReadOnlyList<TerminalProfile> snapshot = settings.Profiles.ToList();
            foreach (TerminalProfile legacy in legacySsh)
            {
                SshProfile mapped = SshConnectionService.MapLegacyTerminalProfile(legacy, snapshot);
                if (string.IsNullOrWhiteSpace(mapped.Host))
                {
                    continue;
                }

                _profileStore.SaveProfile(mapped);
            }

            settings.Profiles = settings.Profiles
                .Where(profile => profile.Type != ConnectionType.SSH)
                .ToList();
            changed = true;
        }

        if (settings.Profiles.Count == 0)
        {
            settings.Profiles = TerminalSettings.GetDefaultProfiles()
                .Where(profile => profile.Type == ConnectionType.Local)
                .ToList();
            changed = true;
        }

        bool defaultValid = settings.Profiles.Any(profile => profile.Id == settings.DefaultProfileId);
        if (!defaultValid)
        {
            settings.DefaultProfileId = settings.Profiles[0].Id;
            changed = true;
        }

        return changed;
    }
}
