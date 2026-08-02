using NovaTerminal.CommandAssist.ShellIntegration.Fish;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// End-to-end tests for the Fish bootstrap. Skipped at runtime when fish
/// is not on PATH; runs on Linux/macOS CI where fish is installable.
/// Spawns fish with XDG_CONFIG_HOME pointed at our temp directory so the
/// generated <root>/fish/config.fish is the one loaded.
/// </summary>
[Trait("Category", "ShellIntegration")]
[Collection(nameof(ShellIntegrationCollection))]
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

    private HarnessResult RunFish(string stdin, string? extraInitLine = null)
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

        // The bootstrap explicitly sources "$HOME/.config/fish/config.fish" as the
        // user's config. HOME is redirected above, and XDG_CONFIG_HOME points at
        // <root> (not <root>/.config), so this file is a distinct, test-owned config
        // and there is no risk of the bootstrap sourcing itself.
        if (extraInitLine is not null)
        {
            string userConfigDir = Path.Combine(_tempRoot, ".config", "fish");
            Directory.CreateDirectory(userConfigDir);
            File.WriteAllText(Path.Combine(userConfigDir, "config.fish"), extraInitLine + "\n");
        }

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
    public void Bootstrap_EmitsCommandStartMarkPastThePromptText()
    {
        // fish_prompt is copied aside and re-defined as "original, then B", so the mark
        // is parsed once the user's prompt has been painted and the cursor sits on the
        // first cell of the user's input -- exactly the prompt's display width. The
        // exact column is what proves the copy ran *before* the mark and emitted the
        // user's prompt byte-for-byte; "> 0" would pass even if the wrapper had painted
        // something of its own.
        const string prompt = "nova-test$ ";
        HarnessResult result = RunFish(
            "exit 0\n",
            extraInitLine: $"function fish_prompt; printf '{prompt}'; end");

        var marks = result.Events.Where(e => e.Kind == "B").ToList();
        Assert.NotEmpty(marks);
        Assert.Contains(marks, m => m.MarkPosition is { } p && p.column == prompt.Length);
    }

    [Fact]
    public void Bootstrap_DoesNotAccumulatePromptMarksAcrossPromptCycles()
    {
        // fish wraps fish_prompt exactly once at bootstrap (guarded against a
        // re-source), so unlike bash/zsh there is no per-cycle string to grow. Pin it
        // at runtime anyway: the wrap is the one place a recursive or repeated
        // redefinition would show up, as a burst of B per prompt.
        HarnessResult result = RunFish(
            "true\ntrue\nexit 0\n",
            extraInitLine: "function fish_prompt; printf 'nova-test$ '; end");

        int prompts = result.Events.Count(e => e.Kind == "A");
        int marks = result.Events.Count(e => e.Kind == "B");

        Assert.True(marks <= prompts * 2,
            $"expected at most one B per prompt repaint, got {marks} B for {prompts} A");
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
