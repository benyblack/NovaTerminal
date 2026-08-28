using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using SkiaSharp;

namespace NovaTerminal.Shots;

public sealed class ShotContext
{
    private readonly IScenario _scenario;

    public ShotContext(MainWindow window, Driver driver, DemoWorld world, ShotRun run, IScenario scenario)
    {
        Window = window;
        Driver = driver;
        World = world;
        Run = run;
        _scenario = scenario;
    }

    public MainWindow Window { get; }

    public Driver Driver { get; }

    public DemoWorld World { get; }

    public ShotRun Run { get; }

    /// <summary>
    /// Opens a tab through MainWindow's own AddTab, so the pane is wired, registered with the
    /// agent-session registry, and themed exactly as a user-opened tab is.
    /// </summary>
    /// <remarks>
    /// The first call adopts the tab the window opened with instead of adding a second one.
    /// MainWindow always starts a tab on the default profile (MainWindow.axaml.cs, the
    /// TryRestoreStartupSession fallback), so an unconditional AddTab leaves every screenshot with
    /// one more tab than the scenario asked for - two identically labelled tabs in the hero still,
    /// which is also enough to trip the tab strip's collision disambiguator and print a "~a1b2"
    /// hint on both. Adoption is conditional on the running pane actually being this profile, so a
    /// scenario opening a different profile still gets a new tab.
    /// </remarks>
    public TerminalPane OpenTab(TerminalProfile profile)
    {
        var tabs = Window.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("MainWindow has no 'Tabs' control.");

        TerminalPane? startupPane = tabs.Items.Count == 1
            ? FindPane(tabs.Items[0] as TabItem ?? throw new InvalidOperationException("The tab strip holds a non-tab item."))
            : null;

        if (startupPane?.Profile is null || startupPane.Profile.Id != profile.Id)
        {
            Driver.InvokePrivate(Window, "AddTab", profile, SshDiagnosticsLevel.None);
            startupPane = null;
        }

        var selected = tabs.SelectedItem as TabItem
            ?? throw new InvalidOperationException("AddTab did not select a tab.");

        TerminalPane pane = startupPane ?? FindPane(selected)
            ?? throw new InvalidOperationException("The new tab contains no TerminalPane.");

        Driver.WaitFor(
            () => pane.Session is not null && pane.IsProcessRunning,
            TimeSpan.FromSeconds(30),
            $"the shell in the '{profile.Name}' profile to start");

        // A running process is not yet a drawn prompt. Settling here keeps the shell's banner
        // and first prompt out of the middle of the first command's output.
        WaitForQuiet(TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(30), "the shell's first prompt");

        return pane;
    }

    private static TerminalPane? FindPane(Control control) => control switch
    {
        TerminalPane pane => pane,
        ContentControl content when content.Content is Control inner => FindPane(inner),
        Decorator decorator when decorator.Child is Control child => FindPane(child),
        Panel panel => panel.Children.OfType<Control>().Select(FindPane).FirstOrDefault(p => p is not null),
        _ => null
    };

    /// <summary>Sends a command and waits for the pane's output to go quiet.</summary>
    public Task RunCommandAsync(TerminalPane pane, string command)
    {
        ITerminalSession session = pane.Session
            ?? throw new InvalidOperationException("The pane has no session.");

        session.SendInput(command + "\n");
        WaitForQuiet(TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(30), command);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits until the rendered frame stops changing. Sleeping a fixed interval would either
    /// truncate a slow command or waste time on a fast one; comparing frames measures the thing
    /// that actually matters — that the image is finished.
    /// </summary>
    private void WaitForQuiet(TimeSpan quietFor, TimeSpan timeout, string what)
    {
        string? previous = null;
        DateTime quietSince = DateTime.UtcNow;

        Driver.WaitFor(
            () =>
            {
                using SKBitmap frame = Rasterizer.CaptureWindow(Window, 1.0);
                string fingerprint = Fingerprint(frame);

                if (fingerprint != previous)
                {
                    previous = fingerprint;
                    quietSince = DateTime.UtcNow;
                    return false;
                }

                return DateTime.UtcNow - quietSince >= quietFor;
            },
            timeout,
            $"output of '{what}' to settle");
    }

    private static string Fingerprint(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 20);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan()));
    }

    /// <summary>Captures the window and records it in the run manifest.</summary>
    public void Capture(string? suffix = null)
    {
        string name = suffix is null ? _scenario.Spec.Name : $"{_scenario.Spec.Name}-{suffix}";
        string path = Path.Combine(Run.OutputDirectory, $"{name}@{Run.Scale:0}x.png");

        using SKBitmap bitmap = Rasterizer.CaptureWindow(Window, Run.Scale);

        double ink = Rasterizer.InkFraction(bitmap);
        if (ink < 0.01)
        {
            throw new InvalidOperationException(
                $"'{name}' rasterized to a near-uniform image ({ink:P2} ink). That is the blank-raster " +
                "failure mode, not a screenshot. Check that the window laid out and the scenario waited.");
        }

        Rasterizer.WritePng(bitmap, path);

        Run.Record(new ShotAsset(
            Name: name,
            Tier: _scenario.Spec.Tier,
            File: path,
            Width: bitmap.Width,
            Height: bitmap.Height,
            Scenario: _scenario.Spec.Name,
            Commit: Run.Commit,
            Os: Run.Os,
            TimestampUtc: DateTime.UtcNow.ToString("O")));
    }
}
