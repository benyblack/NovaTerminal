using NovaTerminal.CommandAssist.ShellIntegration.Fish;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// End-to-end tests for the Fish bootstrap. Skipped at runtime when fish
/// is not on PATH; runs on Linux/macOS CI where fish is installable.
/// Spawns fish with XDG_CONFIG_HOME pointed at our temp directory so the
/// generated <root>/fish/config.fish is the one loaded.
/// </summary>
[Trait("Category", "ShellIntegration")]
public sealed class FishShellIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapPath;

    public FishShellIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_fish_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _bootstrapPath = FishBootstrapBuilder.WriteScript(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private HarnessResult RunFish(string stdin)
    {
        string? fish = ShellHarness.FindFish();
        if (fish is null)
        {
            Assert.Skip("fish not found on this system");
        }

        // XDG_CONFIG_HOME is the parent of the `fish/` directory; the
        // provider writes config.fish to <root>/fish/config.fish, so
        // <root> is the correct XDG_CONFIG_HOME value.
        string? fishDir = Path.GetDirectoryName(_bootstrapPath);
        string? xdgRoot = fishDir != null ? Path.GetDirectoryName(fishDir) : null;
        Assert.NotNull(xdgRoot);

        var env = new Dictionary<string, string>
        {
            ["XDG_CONFIG_HOME"] = xdgRoot!,
            ["HOME"] = _tempRoot,
        };

        // fish -i reads stdin in interactive mode.
        return ShellHarness.Run(fish, "-i", stdin, env, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Bootstrap_EmitsPromptReadyAndAcceptedAndFinished_ForSimpleCommand()
    {
        HarnessResult result = RunFish("echo hello\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "A");
        Assert.Contains(result.Events, e => e.Kind == "C" && e.DecodedCommand == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
    }

    [Fact]
    public void Bootstrap_ReportsNonZeroExitCode_ForFailingCommand()
    {
        HarnessResult result = RunFish("false\nexit 0\n");

        Assert.Contains(result.Events, e =>
            e.Kind == "D" && e.DecodedFinish.exitCode is { } code && code != 0);
    }

    [Fact]
    public void Bootstrap_DoesNotProduceShellErrors()
    {
        // Catches the macOS/BSD `date +%s%N` portability bug at runtime --
        // would have surfaced as a fish `math` parse error on stderr.
        HarnessResult result = RunFish("exit 0\n");

        string[] errorPatterns =
        {
            "Unknown command",
            "Missing end",
            "%N",
            "Expected",
            "math: Error",
        };

        var offending = result.Stderr.Split('\n')
            .Where(line => errorPatterns.Any(pat => line.Contains(pat, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offending.Count == 0,
            $"Bootstrap produced fish-level errors:\n{string.Join("\n", offending)}");
    }
}
