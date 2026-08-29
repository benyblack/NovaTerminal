using Avalonia.Controls;
using Avalonia.Input;
using NovaTerminal.CommandAssist.Views;
using NovaTerminal.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The command assist popup, opened over a pane whose command history was built by real commands
/// run through the shell — not fixtures the popup was told to display.
/// </summary>
/// <remarks>
/// <para>
/// Does not open <see cref="DemoWorld.DemoProfile"/> like every other scenario. That profile's
/// <c>--norc</c> argument is deliberate everywhere else (see <c>DemoWorld.BuildDemoProfile</c>'s own
/// remarks): bash's <c>no_rc</c> flag suppresses <em>any</em> rc-file sourcing, including a custom one
/// supplied via <c>--rcfile</c>, so <c>BashShellIntegrationProvider</c>'s merged arguments
/// (<c>--rcfile &lt;bootstrap&gt; --noprofile --norc -i</c>) never actually source the bootstrap script
/// - confirmed empirically: three real git commands run against the demo profile produced no
/// <c>history.jsonl</c> at all. This scenario needs the opposite trade, so it opens its own profile
/// (<see cref="BuildCommandAssistProfile"/>) with <c>--norc</c> dropped, letting the bootstrap script
/// run for real.
/// </para>
/// <para>
/// Dropping only <c>--norc</c> was tried first and was not enough: a captured image showed the real
/// <c>behna@LegoinB</c> prompt, because Git for Windows' <c>/etc/bash.bashrc</c> still ran and
/// overwrote the <c>PS1</c> this world sets as a process environment variable (see
/// <c>DemoWorld.ApplyDemoEnvironment</c>'s own remarks - this is exactly the leak <c>--norc</c>
/// exists to prevent everywhere else). <c>DemoWorld.WriteHomeBashrc</c> fixes this: the bootstrap
/// script sources <c>~/.bashrc</c> before doing anything else and only <em>appends</em> its OSC 133
/// mark to whatever <c>PS1</c> it finds there rather than overwriting it, so a <c>~/.bashrc</c> that
/// reassigns <c>PS1</c> back to the demo prompt restores it one step after <c>/etc/bash.bashrc</c>
/// clobbered it, and the mark still lands on the corrected value. Confirmed by looking at the actual
/// captured image, not merely asserted here.
/// </para>
/// </remarks>
internal sealed class CommandAssistScenario : IScenario
{
    /// <summary>
    /// Three real commands sharing a "git" prefix, so ranking against that prefix returns several
    /// rows rather than one isolated hit. Captured into Command Assist's own history store by the
    /// shell's own OSC 133 marks (see <c>TerminalPane.HandleShellIntegrationEventAsync</c>), not
    /// written to the store directly.
    /// </summary>
    private static readonly string[] SeedCommands =
    [
        "git status --short --branch",
        "git log --graph --oneline -5",
        "git diff --stat"
    ];

    private const string TypedPrefix = "git";

    public ShotSpec Spec { get; } = new(
        Name: "command-assist",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The command assist popup open beneath the prompt with several ranked suggestions " +
                "visible, the typed prefix highlighted in each.");

    /// <summary>
    /// Off by default (TerminalSettings.CommandAssistEnabled starts false) - without this,
    /// TerminalPane.EnsureCommandAssistInitialized refuses for the whole pane lifetime, including
    /// the OSC 133 marks the seed commands below emit, and there would be no history to rank.
    /// </summary>
    public Action<TerminalSettings>? Settings => settings => settings.CommandAssistEnabled = true;

    public async Task RunAsync(ShotContext context)
    {
        TerminalProfile commandAssistProfile = BuildCommandAssistProfile(context.World.DemoProfile);
        TerminalPane pane = context.OpenTab(commandAssistProfile);

        foreach (string command in SeedCommands)
        {
            await context.RunCommandAsync(pane, command);
        }

        pane.ToggleCommandAssist();
        context.Driver.Pump(3);

        // Real keystrokes into the live PTY (TerminalView.OnTextInput -> NotifyTypedTextObserved),
        // not a direct session write: Command Assist's ranking is triggered from that UI-level
        // event, so bypassing it would leave the popup exactly as empty as before this line.
        context.Driver.TypeText(TypedPrefix);

        // Down opens the full popup (CommandAssistController.MoveSelectionDown sets
        // IsPopupOpen = true) - the same key a user presses to browse past the single-row
        // passive bubble typing alone shows. See CommandAssistPassiveBubbleTests for the same
        // transition pinned at the controller.
        context.Driver.PressKey(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);

        Grid overlayHost = context.Driver.RequireIn<Grid>(pane, "CommandAssistOverlayHost");
        CommandAssistPopupView popup = context.Driver.RequireIn<CommandAssistPopupView>(pane, "CommandAssistPopup");
        ItemsControl suggestionsList = context.Driver.RequireIn<ItemsControl>(popup, "PopupSuggestionsList");

        context.Driver.WaitFor(
            () => overlayHost.IsVisible && popup.IsVisible && suggestionsList.ItemCount > 1,
            TimeSpan.FromSeconds(5),
            "the command assist popup to open with more than one ranked suggestion, built from real " +
            "shell-integration history");

        context.Capture();
    }

    /// <summary>
    /// A copy of the demo profile with <c>--norc</c> dropped so bash's real shell-integration
    /// bootstrap actually sources (see this class's remarks) - <c>--noprofile</c> stays, so
    /// <c>~/.bash_profile</c>/<c>/etc/profile</c> are still skipped the way every other scenario's
    /// shell skips them.
    /// </summary>
    private static TerminalProfile BuildCommandAssistProfile(TerminalProfile demoProfile) => new()
    {
        Name = demoProfile.Name,
        Command = demoProfile.Command,
        Arguments = "--noprofile -i",
        StartingDirectory = demoProfile.StartingDirectory,
        Type = ConnectionType.Local
    };
}
