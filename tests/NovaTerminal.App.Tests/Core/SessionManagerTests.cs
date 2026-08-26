using NovaTerminal.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Storage;
using System.Threading;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests.Core;

public sealed class SessionManagerTests
{
    [AvaloniaFact]
    public void CreateRestoredTabItem_RecordsStartupRestoreCheckpoints()
    {
        long now = 0;
        var tracker = new StartupPerformanceTracker(
            () => Interlocked.Add(ref now, 5),
            startTimestamp: 0,
            timestampFrequency: 1000);

        StartupPerformanceTracker.SetCurrentForTests(tracker);
        try
        {
            var settings = new TerminalSettings();
            var tabSession = new TabSession
            {
                Title = "Local",
                Root = new PaneNode
                {
                    Type = NodeType.Leaf,
                    Command = "pwsh.exe",
                    Arguments = "-NoLogo",
                    PaneId = Guid.NewGuid().ToString()
                }
            };

            var item = SessionManager.CreateRestoredTabItem(tabSession, settings);
            StartupMetricsSnapshot snapshot = tracker.CreateSnapshot();

            Assert.NotNull(item);
            Assert.NotNull(snapshot.Checkpoints);
            Assert.Contains("SessionManager.RestorePaneTree.LeafStart", snapshot.Checkpoints!.Keys);
            Assert.Contains("SessionManager.RestorePaneTree.LeafCreated", snapshot.Checkpoints.Keys);
            Assert.Contains("SessionManager.CreateRestoredTabItem.ContentCreated", snapshot.Checkpoints.Keys);
        }
        finally
        {
            StartupPerformanceTracker.SetCurrentForTests(null);
        }
    }

    [AvaloniaFact]
    public void CreateRestoredTabContent_UsesProvidedSettingsForPaneInitialization()
    {
        var settings = new TerminalSettings
        {
            FontSize = 17
        };

        var tabSession = new TabSession
        {
            Title = "Local",
            Root = new PaneNode
            {
                Type = NodeType.Leaf,
                Command = "pwsh.exe",
                Arguments = "-NoLogo",
                PaneId = Guid.NewGuid().ToString()
            }
        };

        var content = SessionManager.CreateRestoredTabContent(tabSession, settings);

        var pane = Assert.IsType<TerminalPane>(content);
        var termView = pane.FindControl<TerminalView>("TermView");

        Assert.NotNull(termView);
        Assert.Equal(17, termView!.FontSize);
    }

    // Restoring a session saved on another OS used to be permanently fatal: the pane's persisted
    // Command was only checked for blankness, so a Windows workspace restored "cmd.exe" on Linux,
    // the spawn failed, and the same value was written back out on exit - so every later launch
    // failed identically. The pane now falls back to a shell that exists here.
    [AvaloniaFact]
    public void CreateRestoredTabContent_CommandFromAnotherPlatform_FallsBackToARunnableShell()
    {
        var settings = new TerminalSettings();

        var tabSession = new TabSession
        {
            Title = "Local",
            Root = new PaneNode
            {
                Type = NodeType.Leaf,
                // No ProfileId, so the restore takes the raw-command path rather than resolving
                // a profile - exactly the shape a first-run session file has.
                Command = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe",
                Arguments = "",
                PaneId = Guid.NewGuid().ToString()
            }
        };

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(tabSession, settings));

        Assert.Equal(ShellHelper.GetDefaultShell(), pane.ShellCommand);
    }

    // The arguments belonged to the command that was replaced. Carrying them over would hand the
    // substituted shell another shell's flags - a persisted `cmd.exe /c ...` reaching bash as
    // `/c ...`.
    [AvaloniaFact]
    public void CreateRestoredTabContent_WhenCommandIsSubstituted_DropsTheForeignArguments()
    {
        var settings = new TerminalSettings();

        var tabSession = new TabSession
        {
            Title = "Local",
            Root = new PaneNode
            {
                Type = NodeType.Leaf,
                Command = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe",
                Arguments = OperatingSystem.IsWindows() ? "-lc echo hi" : "/c echo hi",
                PaneId = Guid.NewGuid().ToString()
            }
        };

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(tabSession, settings));

        Assert.Equal(ShellHelper.GetDefaultShell(), pane.ShellCommand);
        Assert.Equal(string.Empty, pane.ShellArgs);
    }

    // A profile-backed pane never reached the raw-command fallback, and normal capture writes a
    // ProfileId for every profile-backed pane - so the common shape of the bug was the uncovered
    // one. Opening Windows settings on Linux keeps the imported cmd.exe profile (validation only
    // *adds* this platform's defaults), the pane's ProfileId resolves to it, and the pane failed to
    // spawn on every launch.
    [AvaloniaFact]
    public void CreateRestoredTabContent_ProfileCommandFromAnotherPlatform_FallsBackToARunnableShell()
    {
        var foreign = new TerminalProfile
        {
            Id = Guid.NewGuid(),
            Name = "Imported",
            Type = ConnectionType.Local,
            Command = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe",
            Arguments = OperatingSystem.IsWindows() ? "-lc echo hi" : "/c echo hi"
        };

        var settings = new TerminalSettings { Profiles = new List<TerminalProfile> { foreign } };
        settings.DefaultProfileId = foreign.Id;

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(LeafTabWithProfile(foreign.Id), settings));

        Assert.Equal(ShellHelper.GetDefaultShell(), pane.ShellCommand);
        Assert.Equal(string.Empty, pane.ShellArgs);
    }

    // The substitution must not reach the stored profile. TryResolvePaneProfile returns the instance
    // that lives in TerminalSettings.Profiles, so editing it in place would rewrite the user's own
    // profile - and persist that rewrite the next time settings were saved.
    [AvaloniaFact]
    public void CreateRestoredTabContent_WhenAProfileCommandIsSubstituted_TheStoredProfileIsUntouched()
    {
        string foreignCommand = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe";
        const string foreignArguments = "--some-foreign-flag";

        var stored = new TerminalProfile
        {
            Id = Guid.NewGuid(),
            Name = "Imported",
            Type = ConnectionType.Local,
            Command = foreignCommand,
            Arguments = foreignArguments
        };

        var settings = new TerminalSettings { Profiles = new List<TerminalProfile> { stored } };
        settings.DefaultProfileId = stored.Id;

        SessionManager.CreateRestoredTabContent(LeafTabWithProfile(stored.Id), settings);

        Assert.Equal(foreignCommand, stored.Command);
        Assert.Equal(foreignArguments, stored.Arguments);
        Assert.Same(stored, settings.Profiles[0]);
    }

    [AvaloniaFact]
    public void CreateRestoredTabContent_RunnableProfileCommand_IsUsedAsItStands()
    {
        var runnable = new TerminalProfile
        {
            Id = Guid.NewGuid(),
            Name = "Local",
            Type = ConnectionType.Local,
            Command = ShellHelper.GetDefaultShell(),
            Arguments = ""
        };

        var settings = new TerminalSettings { Profiles = new List<TerminalProfile> { runnable } };
        settings.DefaultProfileId = runnable.Id;

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(LeafTabWithProfile(runnable.Id), settings));

        Assert.Equal(runnable.Command, pane.ShellCommand);
        // The stored instance is handed straight through when nothing needed changing.
        Assert.Same(runnable, pane.Profile);
    }

    // SSH profiles have their command built for them, so the local-executable check must not touch
    // one - substituting a shell there would break the connection rather than repair it.
    [AvaloniaFact]
    public void CreateRestoredTabContent_SshProfile_IsNotTreatedAsALocalCommand()
    {
        var ssh = new TerminalProfile
        {
            Id = Guid.NewGuid(),
            Name = "Remote",
            Type = ConnectionType.SSH,
            // Deliberately unspawnable as a local command: if the local substitution ever applied
            // to SSH profiles, this is what it would replace.
            Command = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe",
            SshHost = "example.internal",
            SshUser = "ops"
        };

        var settings = new TerminalSettings { Profiles = new List<TerminalProfile> { ssh } };

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(LeafTabWithProfile(ssh.Id), settings));

        Assert.NotNull(pane.Profile);
        Assert.Equal(ConnectionType.SSH, pane.Profile!.Type);
        Assert.Same(ssh, pane.Profile);
    }

    // A relative command is not addressed to another OS, so it is kept on both platforms and the
    // stored profile is handed straight through. This test has been wrong twice, in both directions,
    // while the predicate underneath it changed - first asserting the Unix outcome unconditionally,
    // then asserting a Windows substitution that the narrowed predicate no longer performs. Neither
    // platform needs a special case now, which is the point.
    [AvaloniaFact]
    public void CreateRestoredTabContent_RelativeProfileCommand_IsKeptOnEveryPlatform()
    {
        var profile = new TerminalProfile
        {
            Id = Guid.NewGuid(),
            Name = "Project shell",
            Type = ConnectionType.Local,
            Command = "./tools/shell",
            Arguments = "--login",
            StartingDirectory = Path.GetTempPath()
        };

        var settings = new TerminalSettings { Profiles = new List<TerminalProfile> { profile } };

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(LeafTabWithProfile(profile.Id), settings));

        // No filesystem lookup happens, so whether the file is there does not enter into it: a
        // command that cannot be found fails visibly at spawn rather than being swapped out.
        Assert.Same(profile, pane.Profile);
        Assert.Equal("./tools/shell", pane.Profile!.Command);
        Assert.Equal("--login", pane.Profile.Arguments);
    }

    private static TabSession LeafTabWithProfile(Guid profileId) => new()
    {
        Title = "Restored",
        Root = new PaneNode
        {
            Type = NodeType.Leaf,
            ProfileId = profileId.ToString(),
            PaneId = Guid.NewGuid().ToString()
        }
    };

    // The flip side: a command that does run here keeps both itself and its arguments.
    [AvaloniaFact]
    public void CreateRestoredTabContent_RunnableCommand_KeepsItsArguments()
    {
        var settings = new TerminalSettings();

        var tabSession = new TabSession
        {
            Title = "Local",
            Root = new PaneNode
            {
                Type = NodeType.Leaf,
                Command = ShellHelper.GetDefaultShell(),
                Arguments = "--version",
                PaneId = Guid.NewGuid().ToString()
            }
        };

        var pane = Assert.IsType<TerminalPane>(
            SessionManager.CreateRestoredTabContent(tabSession, settings));

        Assert.Equal(ShellHelper.GetDefaultShell(), pane.ShellCommand);
        Assert.Equal("--version", pane.ShellArgs);
    }

    [AvaloniaFact]
    public void RestoreSession_UsesStoreBackedSshProfileAndPreservesBackendKind()
    {
        string storePath = JsonSshProfileStore.GetDefaultStorePath();
        string storeDirectory = Path.GetDirectoryName(storePath)!;
        string backupPath = storePath + ".task5-test-backup";
        bool hadExisting = File.Exists(storePath);

        Directory.CreateDirectory(storeDirectory);
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        if (hadExisting)
        {
            File.Copy(storePath, backupPath, overwrite: true);
        }

        try
        {
            var store = new JsonSshProfileStore(storePath);
            Guid sshId = Guid.Parse("4bcf6934-c30d-4100-99e8-a9b5a283fc5d");
            store.SaveProfile(new SshProfile
            {
                Id = sshId,
                Name = "Native SSH",
                Host = "native.internal",
                User = "ops",
                Port = 22,
                BackendKind = SshBackendKind.Native
            });

            var session = new NovaSession
            {
                Tabs =
                {
                    new TabSession
                    {
                        Title = "Native SSH",
                        Root = new PaneNode
                        {
                            Type = NodeType.Leaf,
                            ProfileId = sshId.ToString(),
                            SshProfileId = sshId.ToString(),
                            PaneId = Guid.NewGuid().ToString()
                        }
                    }
                }
            };

            var tabs = new TabControl();
            var window = new Window();
            var settings = new TerminalSettings
            {
                Profiles = new List<TerminalProfile>
                {
                    new TerminalProfile
                    {
                        Id = Guid.Parse("6f9c6f43-f1e8-4873-ac64-08ae12722b9d"),
                        Name = "Local",
                        Type = ConnectionType.Local,
                        Command = "pwsh.exe"
                    }
                }
            };
            settings.DefaultProfileId = settings.Profiles[0].Id;

            SessionManager.RestoreSession(window, tabs, settings, session);

            var tab = Assert.IsType<TabItem>(Assert.Single(tabs.Items));
            var pane = Assert.IsType<TerminalPane>(tab.Content);
            Assert.NotNull(pane.Profile);
            Assert.Equal(ConnectionType.SSH, pane.Profile!.Type);
            Assert.Equal(SshBackendKind.Native, pane.Profile.SshBackendKind);
        }
        finally
        {
            if (hadExisting)
            {
                File.Copy(backupPath, storePath, overwrite: true);
                File.Delete(backupPath);
            }
            else if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }
}
