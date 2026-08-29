using Avalonia.Controls;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The remote files sidebar beside a terminal pane, with the transfer centre visible showing
/// recent transfers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately NOT registered in ScenarioCatalog.</b> <see cref="TerminalPane.ToggleRemoteFilesSidebar"/>
/// is the real, public entry point the brief asks for, but its private implementation
/// (<c>TerminalPane.IsRemoteFilesSidebarSupported</c>, <c>Controls/TerminalPane.axaml.cs:941-945</c>)
/// requires <c>Profile.Type == ConnectionType.SSH &amp;&amp; Profile.SshBackendKind == SshBackendKind.Native</c>
/// — a genuinely connected native SSH session, not merely a profile of that type. The sidebar's
/// contents come from <c>RemoteFilesSidebarViewModel.OpenAsync</c>, which lists a directory over that
/// live session; there is nothing to list without one.
/// </para>
/// <para>
/// <b>DemoWorld has no reachable SSH server to connect to.</b> Every scenario's pane runs the demo
/// profile — a local bash shell (<c>DemoWorld.BuildDemoProfile</c>) — and the harness has no local SSH
/// daemon of its own. The product's own SSH E2E tests exist
/// (<c>tests/NovaTerminal.Platform.Tests/Ssh/DockerSshFixture.cs</c>) but stand up a Docker container,
/// which this harness cannot depend on without contradicting its own design constraints: it is meant
/// to be fast, dependency-free and fully offline (<c>scripts/shots.ps1 all</c> currently runs eleven
/// scenarios in about 75 seconds with nothing beyond git and bash on PATH).
/// </para>
/// <para>
/// This file still runs <see cref="TerminalPane.ToggleRemoteFilesSidebar"/> for real against the demo
/// (local) profile, which the real code refuses for exactly the reason above, and then waits for
/// <c>RemoteEntriesList</c> to report at least one row before ever calling
/// <see cref="ShotContext.Capture"/> — so if this is ever registered again before the underlying gap
/// is closed, it fails loudly with a timeout instead of quietly publishing an empty sidebar. That is
/// the brief's own instruction ("the scenario must fail rather than capture an empty sidebar") turned
/// into code rather than left as a comment.
/// </para>
/// <para>
/// <b>What would re-enable it.</b> A way for this offline harness to hand a pane a genuinely connected
/// native SSH session with a small, controlled remote filesystem to browse — for example, an in-process
/// fake transport wired through the same <c>SshBackendKind.Native</c> path the product itself uses,
/// rather than a real network connection. That is a change to the SSH stack under <c>src/</c>, which
/// this task may not touch, and a shortcut version of it (setting <c>IsVisible</c> on a sidebar with no
/// backing session, or wiring a decoder the product does not ship) is exactly the dishonest screenshot
/// this project exists to refuse — see the sixel/iTerm2 scenarios' own header comments for the same
/// call made the same way.
/// </para>
/// </remarks>
internal sealed class RemoteFilesScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "remote-files",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The remote files sidebar open beside a terminal pane, with the transfer centre " +
                "visible in the lower right showing recent transfers.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");

        pane.ToggleRemoteFilesSidebar();
        context.Driver.Pump(3);

        context.Driver.Require<Border>("TransferOverlay").IsVisible = true;
        context.Driver.Pump(3);

        // Fails loudly rather than capturing whatever is on screen. Confirmed by actually running
        // this against the demo (local) profile: IsRemoteFilesSidebarSupported() refuses before
        // TerminalPane.EnsureRemoteFilesSidebarHost ever runs, so RemoteFilesSidebar - and its
        // RemoteEntriesList - never exists in the pane's visual tree at all, and RequireIn throws
        // immediately ("No control named 'RemoteEntriesList'") rather than this WaitFor ever
        // reaching its 10s timeout. Either shape is an immediate, loud failure - never a captured
        // image - which is the property that matters; the timeout stays as the correct behaviour
        // for the day a live session makes the control exist but starts empty.
        context.Driver.WaitFor(
            () => context.Driver.RequireIn<ListBox>(pane, "RemoteEntriesList").ItemCount > 0,
            TimeSpan.FromSeconds(10),
            "the remote files sidebar to list at least one real entry from a live SSH session");

        context.Capture();
    }
}
