using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The connection manager overlay, listing SSH profiles over a populated terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately NOT registered in ScenarioCatalog, and DemoWorld.SeedSettings deliberately does
/// NOT seed the two fictional SSH profiles the task brief asks for.</b> Both were tried; both were
/// reverted after they were empirically shown to write into, and read out of, a real per-machine
/// file this harness cannot sandbox. This is a different failure than "the sidebar is empty" — the
/// real risk is a genuine user's real SSH hosts and usernames rendering into a published screenshot,
/// or this harness overwriting a real user's saved connections. See the evidence trail below before
/// re-attempting this scenario.
/// </para>
/// <para>
/// <b>The real command-palette entry does exist</b> (brief Step 3's first instruction, satisfied):
/// <c>CommandRegistry.Register("Connections", "General", () =&gt; ToggleConnections(), ...)</c>
/// (<c>MainWindow.axaml.cs:5624</c>), backed by the private <c>MainWindow.ToggleConnections()</c>
/// (<c>MainWindow.axaml.cs:266-291</c>). That method is the one this scenario would call through
/// <c>Driver.InvokePrivate</c> — the same reflection pattern already used for
/// <c>ToggleCommandPalette</c> and <c>AddTab</c> elsewhere in this harness — rather than setting
/// <c>ConnectionOverlay.IsVisible</c> directly as the brief's fallback sketch does.
/// </para>
/// <para>
/// <b>The blocker is what that method loads, not how it is invoked.</b>
/// <c>ToggleConnections()</c> calls <c>EnsureConnectionManagerControl()</c>
/// (<c>MainWindow.axaml.cs:6078-6128</c>), which does
/// <c>connManager.LoadProfiles(_sshConnectionService.GetConnectionProfiles())</c>. Tracing that
/// service:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>MainWindow</c> constructs it with no override — <c>_sshConnectionService = new
/// SshConnectionService();</c> (<c>MainWindow.axaml.cs:2532</c>) — and
/// <c>_sshLegacyMigrationService = new SshLegacyProfileMigrationService();</c>
/// (<c>MainWindow.axaml.cs:2534</c>), immediately followed by
/// <c>_sshLegacyMigrationService.MigrateLegacyProfiles(_settings)</c>
/// (<c>MainWindow.axaml.cs:2536-2539</c>), which runs on <em>every</em> MainWindow construction, not
/// only when Connection Manager is opened.
/// </item>
/// <item>
/// Both services default their <c>ISshProfileStore</c> to <c>new JsonSshProfileStore()</c>
/// (<c>SshConnectionService.cs:36</c>, <c>SshLegacyProfileMigrationService.cs:22</c>).
/// </item>
/// <item>
/// <c>JsonSshProfileStore</c>'s parameterless path is
/// <c>Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)/NovaTerminal/ssh/profiles.json</c>
/// (<c>JsonSshProfileStore.cs:28-32</c>) — computed directly from the OS special-folder API, never
/// through <c>AppPaths</c>. Everything else this harness touches (settings, themes, sessions, the
/// SSH directory's own scaffolding) resolves through <c>AppPaths.RootDirectory</c>
/// (<c>AppPaths.cs:18-32</c>), which explicitly checks <c>NOVATERM_APPDATA_ROOT</c> — the environment
/// override <c>DemoWorld.Create</c> sets before anything else runs (<c>DemoWorld.cs:62</c>) — and
/// that is exactly the check <c>JsonSshProfileStore.GetDefaultStorePath()</c> skips. So this one path
/// resolves to the real developer's real <c>%LOCALAPPDATA%</c>, not this run's isolated profile root,
/// no matter what <c>NOVATERM_APPDATA_ROOT</c> says.
/// </item>
/// </list>
/// <para>
/// <b>Confirmed empirically, not just by reading the code.</b> The brief's exact two profiles were
/// added to <c>DemoWorld.SeedSettings</c>'s shared baseline and one unrelated Tier-1 scenario
/// (<c>search-overlay</c>, which never touches SSH) was run once. That alone was enough:
/// <c>MigrateLegacyProfiles</c> fired during that scenario's ordinary <c>MainWindow</c> construction
/// and wrote a fictional <c>edge-01.demo.internal</c> profile into this machine's real
/// <c>%LOCALAPPDATA%\NovaTerminal\ssh\profiles.json</c> — a file that already held this developer's
/// seven genuine saved SSH connections (real hostnames, a real IP address, real usernames). Repeating
/// the run left three duplicate fictional entries alongside them. Had <c>ToggleConnections()</c> been
/// invoked in that same run, the resulting screenshot would have shown those seven real profiles —
/// exactly the leak "no published image may contain a real username, hostname, path" exists to
/// prevent, and on any other machine that has ever saved a real SSH profile, not only this one. The
/// pollution was reverted by hand immediately after being found (the three fictional entries removed,
/// the seven genuine ones left untouched) rather than left in place.
/// </para>
/// <para>
/// <b>What would re-enable this scenario.</b> A <c>src/</c> change routing
/// <c>JsonSshProfileStore</c>'s default store path (or the two services' default construction in
/// <c>MainWindow</c>) through <c>AppPaths.SshDirectory</c> — which already resolves correctly under
/// <c>NOVATERM_APPDATA_ROOT</c>, see <c>AppPaths.cs:61</c> and the scaffolding
/// <c>DemoWorld.CreateAppPathsScaffolding</c> already creates for it — so that a sandboxed run reads
/// and writes only its own isolated profile store. That is a production change and out of scope for
/// this task ("zero production changes... nothing under src/"). Once it lands, this scenario can seed
/// its own two fictional profiles (scoped to this scenario's <c>Settings</c> override, not the shared
/// baseline every scenario inherits) and call <c>Driver.InvokePrivate(context.Window,
/// "ToggleConnections")</c> exactly as written below.
/// </para>
/// </remarks>
internal sealed class ConnectionManagerScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "connection-manager",
        Tier: 2,
        LogicalWidth: 1280,
        LogicalHeight: 800,
        Intent: "The connection manager overlay listing two SSH profiles with their hosts and users, " +
                "over a populated terminal.");

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

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane pane = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(pane, "clear");
        await context.RunCommandAsync(pane, "bash scripts/nova-banner.sh");

        // The real command-palette entry, reached the same way this harness already reaches every
        // other MainWindow-private toggle (ToggleCommandPalette, AddTab, DisposeAllTabs) - see this
        // class's remarks for why running it is unsafe until src/ stops this from reading and
        // writing a real, unsandboxed file.
        context.Driver.InvokePrivate(context.Window, "ToggleConnections");
        context.Driver.Pump(5);

        // ConnectionManager is a separate compiled UserControl, hosted dynamically inside
        // ConnectionManagerHost rather than declared in MainWindow.axaml - its own x:Name scope,
        // so "ConnectionsList" has to be resolved against the ConnectionManager instance itself,
        // not against the window (the same reason MainWindow.ToggleConnections's own focus-the-
        // search-box line calls connManager.FindControl rather than this.FindControl).
        var connectionManagerHost = context.Driver.Require<ContentControl>("ConnectionManagerHost");
        var connectionManager = connectionManagerHost.Content as ConnectionManager
            ?? throw new InvalidOperationException(
                "ConnectionManagerHost has no ConnectionManager content after ToggleConnections - " +
                "EnsureConnectionManagerControl did not run or was not reached.");

        context.Driver.WaitFor(
            () => context.Driver.RequireIn<ListBox>(connectionManager, "ConnectionsList").ItemCount >= 2,
            TimeSpan.FromSeconds(5),
            "the connection manager to list the two seeded SSH profiles");

        context.Capture();
    }
}
