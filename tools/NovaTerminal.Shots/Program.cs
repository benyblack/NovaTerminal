using NovaTerminal.Shell;

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
            try
            {
                // Re-seeded per scenario, before the window exists, so a scenario that needs a
                // different theme or tab orientation gets it applied at construction time.
                world.SeedSettings(scenario.Settings);

                await host.RunAsync(async () =>
                {
                    var window = new MainWindow(AppServices.BuildForDesigner())
                    {
                        Width = scenario.Spec.LogicalWidth,
                        Height = scenario.Spec.LogicalHeight
                    };

                    var driver = new Driver(window);

                    try
                    {
                        window.Show();
                        driver.Pump(5);

                        await scenario.RunAsync(new ShotContext(window, driver, world, run, scenario));
                    }
                    finally
                    {
                        window.Close();
                        driver.Pump(3);
                    }
                });

                Console.WriteLine($"[shots] {scenario.Spec.Name} ok");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[shots] {scenario.Spec.Name} FAILED: {ex.Message}");
            }
        }

        run.WriteManifest();
        Console.WriteLine($"[shots] {run.Assets.Count} asset(s) in {outputDirectory}");

        return failures == 0 ? 0 : 1;
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
}
