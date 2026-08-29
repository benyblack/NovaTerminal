using NovaTerminal.Shell;
using NovaTerminal.Shots.Scenarios;
using SkiaSharp;

namespace NovaTerminal.Shots;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            foreach (IScenario scenario in ScenarioCatalog.All().OrderBy(s => s.Spec.Tier))
            {
                Console.WriteLine($"{scenario.Spec.Tier}  {scenario.Spec.Name,-24}{scenario.Spec.Intent}");
            }

            return 0;
        }

        string outputDirectory = ArgumentValue(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "shots");
        double scale = double.TryParse(ArgumentValue(args, "--scale"), out double parsed) ? parsed : 2.0;

        IReadOnlyList<IScenario> requested = ResolveScenarios(args);
        if (requested.Count == 0)
        {
            Console.Error.WriteLine("No matching scenarios. Use --list to see them.");
            return 1;
        }

        string baseDirectory = Path.Combine(Path.GetTempPath(), "nova-shots", Guid.NewGuid().ToString("N"));
        using var world = DemoWorld.Create(baseDirectory);
        world.SeedWorkspace();

        var run = new ShotRun(outputDirectory, scale);
        int failures = 0;

        using ShotHost host = ShotHost.Start();

        foreach (IScenario scenario in requested)
        {
            // Snapshotted before PrepareEnvironment runs and restored unconditionally below, so
            // a scenario's process-environment change (e.g. ClipAgentScenario's NOVA_SHOTS_PACE)
            // cannot outlive its own iteration no matter where this iteration fails. Restoring
            // this way - at the invocation site, around the whole loop body - does not depend on
            // a scenario's own RunAsync being entered at all: PrepareEnvironment is invoked
            // before `new MainWindow(...)`, and if construction, Show(), or host.RunAsync itself
            // throws before a scenario's RunAsync (and its own try/finally) ever gets control,
            // the catch below would otherwise swallow the exception with the environment change
            // still live for every later scenario in this process - paced test scripts leaking
            // into the still-capture path this whole mechanism exists to keep unpaced.
            IReadOnlyDictionary<string, string?> environmentSnapshot = SnapshotEnvironmentVariables();

            try
            {
                try
                {
                    // themes-grid is the one scenario that cannot run through the loop body below:
                    // a theme only fully applies at MainWindow construction time (see
                    // IScenario.Settings's remarks), so five different themes need five separate
                    // windows in one pass, not one window whose scenario.RunAsync tries to re-theme
                    // it mid-run. ScenarioCatalog.All() still lists it (for --list and "all"), so it
                    // has to be excluded here rather than left out of the catalogue.
                    if (scenario is ThemesGridScenario themesGrid)
                    {
                        await ComposeThemesGridAsync(host, world, run, themesGrid);
                    }
                    else
                    {
                        await RunScenarioAsync(host, world, run, scenario);
                    }

                    Console.WriteLine($"[shots] {scenario.Spec.Name} ok");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"[shots] {scenario.Spec.Name} FAILED: {ex.Message}");
                }
            }
            finally
            {
                RestoreEnvironmentVariables(environmentSnapshot);
            }
        }

        // Snapshotted before iterating: VariantBuilder.BuildAll records each variant it produces
        // onto this same run, and iterating run.Assets live while it grows out from under the
        // enumerator throws. Tier 3+ masters (ClipAgentScenario's clip-agent) are excluded - the
        // brief scopes derived variants to Tier 1/2 masters only.
        ShotAsset[] masters = run.Assets.Where(a => a.Tier <= 2).ToArray();
        foreach (ShotAsset master in masters)
        {
            VariantBuilder.BuildAll(master, run);
        }

        run.WriteManifest();
        Console.WriteLine($"[shots] {run.Assets.Count} asset(s) in {outputDirectory}");

        if (args.Contains("--publish"))
        {
            // No --repo-root override: shots.ps1 (like every other wrapper in this repo) always
            // invokes the tool from the repository root, which is also --out's own default base
            // a few lines up.
            IReadOnlyList<string> published = Publisher.Publish(run, Directory.GetCurrentDirectory());
            foreach (string path in published)
            {
                Console.WriteLine($"[shots] published {path}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The normal one-window-per-scenario path: seed settings and the environment, construct a
    /// fresh MainWindow, run the scenario's own body, and tear the window down again. Pulled out
    /// of <see cref="Main"/>'s loop so <see cref="ComposeThemesGridAsync"/> can drive the same
    /// window lifecycle several times in one iteration without duplicating it.
    /// </summary>
    private static async Task RunScenarioAsync(ShotHost host, DemoWorld world, ShotRun run, IScenario scenario)
    {
        // Re-seeded per scenario, before the window exists, so a scenario that needs a
        // different theme or tab orientation gets it applied at construction time.
        world.SeedSettings(scenario.Settings);

        // And started from a clean window: the previous scenario's teardown saved its tabs,
        // and MainWindow restores them on the next start.
        world.ForgetPreviousSession();

        // Also before construction, and for the same reason Settings is: MainWindow
        // spawns (or restores) its startup tab's shell during construction/Show(), so an
        // environment variable that shell must inherit has to be set before `new
        // MainWindow(...)` runs, not from inside RunAsync - see PrepareEnvironment's
        // remarks for why "inside RunAsync" is too late for every scenario's first pane.
        scenario.PrepareEnvironment?.Invoke();

        await host.RunAsync(async () =>
        {
            var window = new MainWindow(AppServices.BuildForDesigner())
            {
                Width = scenario.Spec.LogicalWidth,
                Height = scenario.Spec.LogicalHeight
            };

            var driver = new Driver(window);
            var context = new ShotContext(window, driver, world, run, scenario);

            try
            {
                window.Show();
                driver.Pump(5);

                await scenario.RunAsync(context);
            }
            finally
            {
                // Before Close(), not after and not instead: closing the window tears down
                // timers and the agent host but leaves every pane - and every shell - alive,
                // and this process outlives the scenario.
                //
                // window.Close() is nested in its own finally so it runs even if
                // DisposePanes() throws (its WaitFor has a 30s deadline; InvokePrivate
                // throws if DisposeAllTabs is ever renamed). Skipping Close() then skips
                // PerformAppTeardown entirely, and its timers, global hotkey and
                // agent-host hooks would stay live into the next scenario's MainWindow -
                // which matters once a scenario drives the agent host.
                try
                {
                    context.DisposePanes();
                }
                finally
                {
                    window.Close();
                    driver.Pump(3);
                }
            }
        });
    }

    /// <summary>
    /// Renders themes-grid: one MainWindow per theme in <see cref="ThemesGridScenario.Themes"/>,
    /// each replaying hero-single's transcript, tiled together at the end with
    /// <see cref="PostProcess.Grid"/>. This is not a variant of <see cref="RunScenarioAsync"/>
    /// because it needs several full window lifecycles - not one - inside a single manifest entry,
    /// and it must capture each pass itself rather than through <see cref="ShotContext.Capture"/>,
    /// which would write (and record) five separate PNGs nobody asked for.
    /// </summary>
    private static async Task ComposeThemesGridAsync(ShotHost host, DemoWorld world, ShotRun run, ThemesGridScenario scenario)
    {
        var tiles = new List<SKBitmap>();

        try
        {
            foreach (string themeName in ThemesGridScenario.Themes)
            {
                world.SeedSettings(settings => settings.ThemeName = themeName);

                // ThemeManager.GetTheme falls back to "Default" silently on a miss - see
                // ThemesGridScenario.Themes's remarks. Checked here, immediately after seeding and
                // before a window is even constructed, so a wrong name fails loudly and by name
                // instead of producing a grid of plausible-but-wrong tiles. A fresh ThemeManager,
                // not one pulled off some settings instance: it reads AppPaths.ThemesDirectory,
                // which DemoWorld.Create already pointed at this world's isolated root via
                // NOVATERM_APPDATA_ROOT, so this sees exactly what MainWindow's own settings load
                // will see once the window below is constructed.
                string loadedThemeName = new ThemeManager().GetTheme(themeName).Name;
                if (!string.Equals(loadedThemeName, themeName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Theme '{themeName}' did not load: ThemeManager.GetTheme silently returned " +
                        $"'{loadedThemeName}' instead. Check that the name matches the theme JSON's " +
                        "\"Name\" field exactly (spaces included), not its filename.");
                }

                world.ForgetPreviousSession();

                await host.RunAsync(async () =>
                {
                    var window = new MainWindow(AppServices.BuildForDesigner())
                    {
                        Width = scenario.Spec.LogicalWidth,
                        Height = scenario.Spec.LogicalHeight
                    };

                    var driver = new Driver(window);
                    var context = new ShotContext(window, driver, world, run, scenario);

                    try
                    {
                        window.Show();
                        driver.Pump(5);

                        await HeroSingleScenario.PlayAsync(context);

                        tiles.Add(Rasterizer.CaptureWindow(window, run.Scale));
                    }
                    finally
                    {
                        try
                        {
                            context.DisposePanes();
                        }
                        finally
                        {
                            window.Close();
                            driver.Pump(3);
                        }
                    }
                });
            }

            using SKBitmap grid = PostProcess.Grid(
                tiles,
                columns: 2,
                gap: 24,
                background: new SKColor(0x0E, 0x10, 0x14));

            string path = Path.Combine(run.OutputDirectory, $"{scenario.Spec.Name}@{run.Scale:0}x.png");
            Rasterizer.WritePng(grid, path);

            run.Record(new ShotAsset(
                Name: scenario.Spec.Name,
                Tier: scenario.Spec.Tier,
                File: path,
                Width: grid.Width,
                Height: grid.Height,
                Scenario: scenario.Spec.Name,
                Commit: run.Commit,
                Os: run.Os,
                TimestampUtc: DateTime.UtcNow.ToString("O")));
        }
        finally
        {
            foreach (SKBitmap tile in tiles)
            {
                tile.Dispose();
            }
        }
    }

    /// <summary>
    /// The scenario names on the command line: every token that is neither a <c>--flag</c> nor
    /// the value belonging to one.
    /// </summary>
    /// <remarks>
    /// The flag-value skip is why this is not a one-line Where. Filtering only on the <c>--</c>
    /// prefix leaves <c>2</c> from <c>--scale 2</c> looking exactly like a scenario name, so
    /// <c>shots hero-single --scale 2</c> would resolve to hero-single plus an unknown name -
    /// silently dropped by OfType, but a run of <c>--scale 2</c> alone would then resolve to
    /// nothing at all rather than to every scenario.
    /// </remarks>
    private static IReadOnlyList<IScenario> ResolveScenarios(string[] args)
    {
        var skip = new HashSet<int>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                skip.Add(i + 1);
            }
        }

        string[] names = args
            .Where((a, i) => !skip.Contains(i) && !a.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        if (names.Length == 0 || names.Contains("all", StringComparer.OrdinalIgnoreCase))
        {
            return ScenarioCatalog.All();
        }

        return names.Select(ScenarioCatalog.Find).OfType<IScenario>().ToArray();
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Captures every process environment variable's current value, so a later call to
    /// <see cref="RestoreEnvironmentVariables"/> can put the whole environment back exactly as
    /// it was - including undoing a variable a scenario's <see cref="IScenario.PrepareEnvironment"/>
    /// added that did not exist before it ran.
    /// </summary>
    private static Dictionary<string, string?> SnapshotEnvironmentVariables()
    {
        var snapshot = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            snapshot[(string)entry.Key] = (string?)entry.Value;
        }

        return snapshot;
    }

    /// <summary>
    /// Puts the process environment back to exactly what <paramref name="snapshot"/> recorded:
    /// restores anything a scenario changed and removes anything it added. This is the generic,
    /// invocation-site backstop for <see cref="IScenario.PrepareEnvironment"/> - it does not know
    /// or care which variables any given scenario touched, so it cannot be defeated by a scenario
    /// that forgets to clean up after itself, or by an exception that skips that scenario's own
    /// cleanup entirely (thrown from <c>new MainWindow(...)</c>, <c>window.Show()</c>, or
    /// anywhere else in <c>host.RunAsync</c> before the scenario's own <c>RunAsync</c>, and its
    /// own try/finally, is ever entered).
    /// </summary>
    private static void RestoreEnvironmentVariables(IReadOnlyDictionary<string, string?> snapshot)
    {
        var current = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            current[(string)entry.Key] = (string?)entry.Value;
        }

        foreach (string name in current.Keys)
        {
            if (!snapshot.ContainsKey(name))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        foreach ((string name, string? value) in snapshot)
        {
            if (!current.TryGetValue(name, out string? currentValue) || currentValue != value)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
