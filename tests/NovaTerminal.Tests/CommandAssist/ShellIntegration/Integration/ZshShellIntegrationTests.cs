using NovaTerminal.CommandAssist.ShellIntegration.Zsh;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// End-to-end tests for the Zsh bootstrap. Skipped at runtime when zsh
/// is not on PATH (Windows dev boxes); runs on Linux/macOS CI. Spawns
/// zsh with ZDOTDIR pointed at our temp directory so the generated
/// .zshrc is the one loaded, mirroring the production launch plan.
/// </summary>
[Trait("Category", "ShellIntegration")]
public sealed class ZshShellIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapPath;

    public ZshShellIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_zsh_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _bootstrapPath = ZshBootstrapBuilder.WriteScript(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private HarnessResult RunZsh(string stdin)
    {
        string? zsh = ShellHarness.FindZsh();
        if (zsh is null)
        {
            Assert.Skip("zsh not found on this system");
        }

        // ZDOTDIR points at the parent of <root>/zsh/.zshrc -- i.e. the
        // zsh-only subdirectory the provider writes to, NOT the temp root
        // itself. WriteScript returns the absolute .zshrc path; its
        // containing directory is the correct ZDOTDIR value.
        string? zshDir = Path.GetDirectoryName(_bootstrapPath);
        Assert.NotNull(zshDir);

        var env = new Dictionary<string, string>
        {
            ["ZDOTDIR"] = zshDir!,
            // Redirect HOME so the bootstrap's `source $HOME/.zshrc` doesn't
            // pick up the dev machine's actual user config.
            ["HOME"] = _tempRoot,
        };

        // `--no-global-rcs` skips /etc/zsh/* so the system zshrc doesn't run
        // compinit on us. On a fresh CI runner compinit detects "insecure
        // directories" and prompts `Ignore? [y/n]` BEFORE our bootstrap
        // loads -- which then eats the first line of scripted stdin and
        // leaves zsh wedged on an empty input buffer. With this flag only
        // $ZDOTDIR/.zshrc (our bootstrap) is sourced, matching how the
        // bash test isolates via --rcfile.
        return ShellHarness.Run(zsh, "--no-global-rcs -i", stdin, env, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Bootstrap_EmitsPromptReadyAndAcceptedAndFinished_ForSimpleCommand()
    {
        HarnessResult result = RunZsh("echo hello\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "A");
        Assert.Contains(result.Events, e => e.Kind == "C" && e.DecodedCommand == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
    }

    [Fact]
    public void Bootstrap_ReportsNonZeroExitCode_ForFailingCommand()
    {
        HarnessResult result = RunZsh("false\nexit 0\n");

        Assert.Contains(result.Events, e =>
            e.Kind == "D" && e.DecodedFinish.exitCode is { } code && code != 0);
    }

    [Fact]
    public void Bootstrap_DoesNotProduceShellErrors()
    {
        // Catches the macOS/BSD `date +%s%N` portability bug at runtime --
        // would have surfaced as "bad arithmetic" or similar on stderr.
        HarnessResult result = RunZsh("exit 0\n");

        string[] errorPatterns =
        {
            "command not found",
            "syntax error",
            "bad math",
            "%N",
            "parse error",
        };

        var offending = result.Stderr.Split('\n')
            .Where(line => errorPatterns.Any(pat => line.Contains(pat, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offending.Count == 0,
            $"Bootstrap produced zsh-level errors:\n{string.Join("\n", offending)}");
    }

    [Fact]
    public void Bootstrap_EmitsCwdMarker_WhenWorkingDirectoryChanges()
    {
        HarnessResult result = RunZsh("cd /\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "7" && e.Payload!.StartsWith("file://"));
    }
}
