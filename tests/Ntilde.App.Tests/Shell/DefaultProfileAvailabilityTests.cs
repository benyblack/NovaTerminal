using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Shell;

/// <summary>
/// The default profile list used to advertise shells that were not installed. On a stock
/// Windows machine `pwsh.exe` (PowerShell 7) does not exist, so the "PowerShell" profile
/// was a dead entry that failed to spawn with a raw FFI error. Reported from a real
/// first-run install; see docs/CONFIG_STORAGE_CONTRACT.md for the release context.
/// </summary>
public sealed class DefaultProfileAvailabilityTests
{
    [Fact]
    public void GetDefaultProfiles_OmitsProfilesWhoseCommandIsNotInstalled()
    {
        // Everything present except PowerShell 7 — the exact stock-Windows shape.
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(
            commandExists: command => !command.Contains("pwsh", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            profiles,
            p => p.Command.Contains("pwsh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetDefaultProfiles_KeepsProfilesWhoseCommandIsInstalled()
    {
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(commandExists: _ => true);

        // Nothing was filtered, so the full default set survives.
        Assert.True(profiles.Count >= 3);
    }

    [Fact]
    public void GetDefaultProfiles_NeverReturnsAnEmptyList_EvenWhenNothingIsDetected()
    {
        // A probe can fail wholesale — an empty PATH makes InPath return false for
        // everything. TerminalSettings' constructor does Profiles[0].Id, so an empty
        // list is an IndexOutOfRangeException on startup: worse than a dead profile.
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(commandExists: _ => false);

        Assert.NotEmpty(profiles);
        Assert.All(profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Command)));
    }

    [Fact]
    public void GetDefaultProfiles_ProbesTheCommandNotTheProfileName()
    {
        var probed = new List<string>();

        TerminalSettings.GetDefaultProfiles(commandExists: command =>
        {
            probed.Add(command);
            return true;
        });

        // "PowerShell" is a display name; "pwsh.exe" is what has to exist on disk.
        Assert.DoesNotContain("PowerShell", probed);
        Assert.All(probed, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    // --- The Unix candidates: the login shell leads, and must not duplicate the trio. ---

    [Fact]
    public void UnixCandidates_LeadWithTheLoginShell_WhenItIsNotOneOfTheFixedEntries()
    {
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(
            TerminalSettings.UnixProfileCandidates("/usr/bin/fish"),
            commandExists: _ => true);

        // DefaultProfileId is Profiles[0].Id, so leading with the login shell is what makes
        // first run open the shell the user actually uses.
        Assert.Equal("Default Shell", profiles[0].Name);
        Assert.Equal("/usr/bin/fish", profiles[0].Command);
        Assert.Equal(4, profiles.Count);
        Assert.Equal(4, profiles.Select(p => p.Command).Distinct().Count());
    }

    [Fact]
    public void UnixCandidates_LoginShellAmongTheFixedEntries_IsNotDuplicated()
    {
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(
            TerminalSettings.UnixProfileCandidates("/bin/bash"),
            commandExists: _ => true);

        Assert.Equal("Default Shell", profiles[0].Name);
        Assert.Single(profiles, p => p.Command == "/bin/bash");
        Assert.Equal(3, profiles.Count);
    }

    [Fact]
    public void UnixCandidates_ExistenceFilterStillApplies()
    {
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(
            TerminalSettings.UnixProfileCandidates("/usr/bin/fish"),
            commandExists: command => command != "/bin/zsh");

        Assert.DoesNotContain(profiles, p => p.Command == "/bin/zsh");
        Assert.Equal(3, profiles.Count);
    }

    [Fact]
    public void UnixCandidates_NothingInstalled_FallsToTheGuaranteedFloor()
    {
        List<TerminalProfile> profiles = TerminalSettings.GetDefaultProfiles(
            TerminalSettings.UnixProfileCandidates("/usr/bin/fish"),
            commandExists: _ => false);

        // The floor is platform-shaped: /bin/sh on Unix, cmd.exe where this test runs.
        string floor = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        Assert.Single(profiles);
        Assert.Equal(floor, profiles[0].Command);
    }
}
