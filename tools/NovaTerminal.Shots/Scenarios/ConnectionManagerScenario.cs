using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Services.Ssh;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The Connection Manager window listing two seeded SSH profiles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Previously deferred, now unblocked.</b> This scenario was kept out of
/// <see cref="ScenarioCatalog"/> because <c>JsonSshProfileStore</c> resolved its store path from
/// <c>LocalApplicationData</c> directly and ignored <c>NOVATERM_APPDATA_ROOT</c>, so running it
/// inside this harness's isolated demo world still read and wrote the machine's real
/// <c>ssh/profiles.json</c>. That was confirmed empirically, not just by reading: seeding these two
/// profiles and running one unrelated scenario was enough for MigrateLegacyProfiles to write a
/// fictional edge-01.demo.internal entry into a real file holding seven genuine saved connections.
/// Had the manager been opened in that run, the screenshot would have shown those seven real
/// hostnames and usernames. The pollution was reverted by hand at the time.
/// </para>
/// <para>
/// Both Platform paths now resolve through <c>PlatformAppPaths</c>, which honours the override, so
/// a sandboxed run reads and writes only its own profile store and the two profiles below are the
/// only ones that can appear.
/// </para>
/// <para>
/// <b>Why the window is constructed here rather than opened through MainWindow.</b> The manager is
/// now a real top-level window, and <c>MainWindow.OpenConnectionManagerAsync</c> ends in
/// <c>await window.ShowDialog(this)</c>. ShowDialog hangs headlessly - MainWindow's own palette
/// registration comments call that out as the reason its Backup entries are asserted by reflection
/// rather than invoked - so driving <c>ToggleConnections</c> would wedge the capture rather than
/// photograph it. Constructing <see cref="ConnectionManagerWindow"/> and calling <c>Show()</c> is
/// the same route <see cref="SettingsWindowScenario"/> already takes for SettingsWindow.
///
/// The theme is applied the way production applies it, because the picture depends on it. Nothing
/// else production wires up (the saved-password vault, the quick-open and copy-command callbacks)
/// changes a still image, so none of it is reproduced here - this is not a mock of the manager, it
/// is the real control rendering the real seeded profile store.
/// </para>
/// </remarks>
internal sealed class ConnectionManagerScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "connection-manager",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The Connection Manager window listing two SSH profiles with their user, host and " +
                "port, one of them selected so the detail pane beside the list is filled rather " +
                "than showing its empty-state placeholder.");

    /// <summary>
    /// Scoped to this scenario alone, not DemoWorld.SeedSettings's shared baseline every scenario
    /// inherits — see this class's remarks for why the baseline must never carry SSH-type profiles
    /// until the underlying storage gap is fixed.
    /// </summary>
    public Action<TerminalSettings>? Settings => settings =>
    {
        settings.Profiles.Add(new TerminalProfile
        {
            Name = "edge-01",
            Type = ConnectionType.SSH,
            SshHost = "edge-01.demo.internal",
            SshUser = "nova",
            SshPort = 22
        });
        settings.Profiles.Add(new TerminalProfile
        {
            Name = "build-runner",
            Type = ConnectionType.SSH,
            SshHost = "build.demo.internal",
            SshUser = "ci",
            SshPort = 2222
        });
    };

    // No tab is opened and no command is run first, matching the settings scenarios: this captures
    // the manager window on its own, so nothing behind it reaches the frame and seeding a terminal
    // would only be work that never appears in the picture.
    public Task RunAsync(ShotContext context)
    {
        var window = new ConnectionManagerWindow
        {
            Width = 980,
            Height = 620
        };

        // Show/Pump inside the try for the same reason SettingsWindowScenario does it: Pump runs
        // Dispatcher.UIThread.RunJobs(), so a throw there would otherwise leak a live Window with
        // nothing left to close it.
        try
        {
            ConnectionManager manager = window.Manager
                ?? throw new InvalidOperationException(
                    "ConnectionManagerWindow has no ManagerControl. The window markup changed - " +
                    "update the scenario.");

            // Loaded rather than reached out of MainWindow's private field: AppPaths resolves from
            // NOVATERM_APPDATA_ROOT live, so this reads the very settings.json DemoWorld seeded for
            // this run - the same route Program.BuildSeededServices takes for the main window.
            manager.ApplyTheme(TerminalSettings.Load().ActiveTheme);

            // The list is fed from the SSH profile store, not from settings.Profiles. The two
            // profiles seeded above reach that store the ordinary way - MainWindow's constructor
            // runs SshLegacyProfileMigrationService over the seeded settings before this scenario
            // is entered - and SshConnectionService reads it back through JsonSshProfileStore,
            // which now resolves under NOVATERM_APPDATA_ROOT. That migration writing to an
            // unsandboxed store is precisely what used to make this scenario unsafe to run.
            window.LoadProfiles(new SshConnectionService().GetConnectionProfiles());

            window.Show();
            context.Driver.Pump(5);

            context.Driver.WaitFor(
                () => context.Driver.RequireIn<ListBox>(manager, "ConnectionsList").ItemCount >= 2,
                TimeSpan.FromSeconds(5),
                "the connection manager to list the two seeded SSH profiles");

            // Selecting one is what fills the right-hand pane. Left unselected, well over a third
            // of the window is the "Select a connection" empty state, which photographs as an
            // unfinished feature rather than a populated manager.
            ListBox connections = context.Driver.RequireIn<ListBox>(manager, "ConnectionsList");
            connections.SelectedIndex = 0;
            context.Driver.Pump(5);

            context.Driver.WaitFor(
                () => connections.SelectedItem != null,
                TimeSpan.FromSeconds(5),
                "the selected connection's detail pane to populate");

            context.CaptureOther(window, "window");
        }
        finally
        {
            window.Close();
            context.Driver.Pump(3);
        }

        return Task.CompletedTask;
    }
}
