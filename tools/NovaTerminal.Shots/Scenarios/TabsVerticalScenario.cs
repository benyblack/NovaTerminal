using System.Reflection;
using Avalonia.Controls;
using NovaTerminal.AgentHost;
using NovaTerminal.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The vertical tab strip, with five distinctly named tabs so the layout reads as a real sidebar
/// rather than a single-tab strip that merely happens to be tall.
/// </summary>
/// <remarks>
/// Naming the tabs is not as simple as setting each clone's <c>Profile.Name</c>: every pane in
/// this harness runs DemoWorld's shared prompt, which opens with a literal OSC-0 title escape
/// (<c>\e]0;nova-demo\a</c>) — that is what <c>TerminalPane.GetBaseTabTitle</c> actually reads,
/// ahead of the profile name, and it fires on every prompt redraw. Renaming the tabs therefore
/// goes through the same route the UI's own "Rename Tab" command uses (<c>MainWindow</c>'s
/// per-tab <c>UserTitle</c>, which <c>ResolveTabPrimaryTitle</c> checks before either the OSC
/// title or the profile name) rather than through the prompt at all.
/// </remarks>
internal sealed class TabsVerticalScenario : IScenario
{
    private static readonly string[] TabNames =
        ["claude-code", "codex", "build", "logs", "ssh · edge-01"];

    /// <summary>
    /// The tab that receives an agent-delivered command, so the sidebar's "carrying agent
    /// activity" claim is a real AgentAttentionMachine tier - the same mechanism agent-session
    /// photographs on a pane - rather than a marker written directly into tab state.
    /// </summary>
    private const string AgentTabName = "claude-code";

    /// <summary>
    /// This Intent used to also claim "each showing its status indicator and output preview
    /// line". Both turned out to be structurally unavailable for four of the five tabs, not
    /// just unimplemented: DemoWorld's PS1 is one literal, unchanging string, so every settled
    /// pane's bottom line is byte-identical regardless of which tab it is or what ran in it -
    /// TabPreviewTracker has nothing distinguishing to show once the shell returns to its
    /// prompt. And TabStatusTracker's "Working" window is 2 seconds; five tabs opened and run in
    /// sequence take well over that to get through, so by the final capture only the
    /// most-recently-run tab can still be inside that window - never four of the five at once.
    /// Neither is a bug in this scenario to fix; both are what the real mechanism does when fed
    /// five sequential, near-instant, identically-prompted shells. What the image can actually
    /// show for every tab, reliably, is its distinct name and - for the one selected - a visible
    /// highlight; the agent-activity marker is the one dynamic claim that IS real for one tab.
    /// </summary>
    public ShotSpec Spec { get; } = new(
        Name: "tabs-vertical",
        Tier: 1,
        LogicalWidth: 1440,
        LogicalHeight: 900,
        Intent: "The vertical tab sidebar with five distinctly named tabs, the selected tab " +
                "highlighted, and one tab visibly carrying agent activity.");

    /// <summary>
    /// Applied before MainWindow is constructed, which is the only time TabStripOrientation takes
    /// effect. Act is also enabled here (observe is already on in DemoWorld's baseline settings) -
    /// without it the agent-delivered command below would be refused, and the "one tab visibly
    /// carrying agent activity" half of the Intent would have nothing real to show.
    /// </summary>
    public Action<TerminalSettings>? Settings => settings =>
    {
        settings.TabStripOrientation = "Vertical";
        settings.AgentAccessActEnabled = true;
    };

    public async Task RunAsync(ShotContext context)
    {
        var tabs = context.Window.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("MainWindow has no 'Tabs' control.");

        // The strip being vertical is this scenario's one defining claim, and the brief names
        // silent-horizontal (Settings applied too late, or never) as the expected failure mode -
        // every tab could still open, get named and run its command, and the capture would look
        // identical to a passing run except for the layout itself. ApplyTabLayout's only visible
        // trace of the mode is this class (MainWindow.axaml.cs:887).
        if (!tabs.Classes.Contains("vertical-tabs"))
        {
            throw new InvalidOperationException(
                "The tab strip is not wearing the 'vertical-tabs' class, so it rendered horizontal. " +
                "TabStripOrientation either did not reach MainWindow before construction, or " +
                "ApplyTabLayout no longer sets this class for vertical mode.");
        }

        for (int i = 0; i < TabNames.Length; i++)
        {
            string name = TabNames[i];

            TerminalProfile profile = context.World.DemoProfile.ShallowCopy();
            profile.Name = name;

            // ShallowCopy is MemberwiseClone, which carries DemoProfile's Id along with
            // everything else. OpenTab compares Ids to decide whether to adopt MainWindow's own
            // startup tab instead of adding a second one (see its remarks) - so the first clone
            // deliberately keeps that Id, letting it adopt the startup tab exactly the way
            // hero-split and agent-session do, and only the later clones get a fresh Id. Giving
            // every clone a fresh Id would leave six tabs on screen instead of five: the adopted
            // startup tab plus five new ones.
            if (i > 0)
            {
                profile.Id = Guid.NewGuid();
            }

            TerminalPane pane = context.OpenTab(profile);
            TabItem tab = tabs.SelectedItem as TabItem
                ?? throw new InvalidOperationException($"No tab is selected after opening '{name}'.");

            SetTabTitle(context, tab, name);
            await context.RunCommandAsync(pane, "bash scripts/demo-test.sh");

            if (string.Equals(name, AgentTabName, StringComparison.Ordinal))
            {
                await MarkAgentActivityAsync(context, pane);
                RequireTabLabelContains(tab, MainWindow.AgentWroteGlyph);
            }

            RequireTabLabelContains(tab, name);
        }

        context.Capture();
    }

    /// <summary>
    /// Delivers one command through the agent host, exactly as agent-session does, then asks
    /// MainWindow to recompute each tab's attention marker from the registry and waits for the
    /// dispatch to run. RefreshTabAgentAttention is also wired to fire on its own whenever an
    /// AttentionMachine changes (MainWindow.OnAgentAttentionChangedForTabs), so this call is a
    /// deterministic backstop rather than the only path - the image must not depend on winning a
    /// race with a background dispatcher post.
    /// </summary>
    private static async Task MarkAgentActivityAsync(ShotContext context, TerminalPane pane)
    {
        context.Driver.WaitFor(
            () => AgentSessionRegistry.Instance.TryGet(pane.PaneId, out _),
            TimeSpan.FromSeconds(10),
            "the pane to register with the agent-session registry");

        await context.RunDeliveredCommandAsync(
            pane,
            () => AgentWire.DeliverAsync(pane, "git status --short --branch"),
            "the agent-delivered status check");

        context.Window.RefreshTabAgentAttention();
        context.Driver.Pump(5);
    }

    /// <summary>
    /// Sets the tab's user title through the same per-tab state MainWindow's own "Rename Tab"
    /// command writes to, then asks MainWindow to redraw the header from it.
    /// </summary>
    /// <remarks>
    /// <c>GetOrCreateTabState</c> and the record it returns are both private, so there is no
    /// typed way to reach the <c>UserTitle</c> property; reflection reaches the same effect
    /// "Rename Tab" has after its prompt dialog returns, without having to drive that dialog
    /// headless. <c>UpdateTabVisuals</c> is internal, not private, and this project has
    /// InternalsVisibleTo, so it is called directly rather than through <c>Driver.InvokePrivate</c>.
    /// </remarks>
    private static void SetTabTitle(ShotContext context, TabItem tab, string title)
    {
        object state = context.Driver.InvokePrivate(context.Window, "GetOrCreateTabState", tab)
            ?? throw new InvalidOperationException("GetOrCreateTabState returned null for a real tab.");

        PropertyInfo userTitle = state.GetType().GetProperty("UserTitle", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "MainWindow's per-tab state has no 'UserTitle' property. The markup changed - update the scenario.");

        userTitle.SetValue(state, title);
        context.Window.UpdateTabVisuals(tab);
    }

    /// <summary>
    /// Checks the tab's rendered header - not just the state this scenario wrote - actually
    /// carries <paramref name="expected"/>, reading it the way <see cref="MainWindow.UpdateTabVisuals"/>
    /// left it: the header control's tooltip, which MainWindow sets to the untruncated
    /// <c>BuildFullTabLabel</c> (name plus any attention marker) on every redraw.
    /// </summary>
    private static void RequireTabLabelContains(TabItem tab, string expected)
    {
        string label = tab.Header is Control header ? ToolTip.GetTip(header) as string ?? string.Empty : string.Empty;

        if (!label.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Tab header reads '{label}', which does not contain the expected name '{expected}'. " +
                "SetTabTitle either did not run or MainWindow no longer reads UserTitle the way this " +
                "scenario expects.");
        }
    }
}
