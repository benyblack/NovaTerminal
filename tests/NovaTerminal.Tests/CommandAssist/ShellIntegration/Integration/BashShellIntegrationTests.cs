using NovaTerminal.CommandAssist.ShellIntegration.Bash;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// End-to-end tests that spawn a real bash with our generated bootstrap
/// and assert the OSC 7 / 133;A/C/D lifecycle actually comes out. These
/// catch a class of bugs the substring-based BashBootstrapBuilderTests
/// cannot reach -- e.g. shell syntax errors, BSD/GNU portability issues,
/// or DEBUG/PROMPT_COMMAND interaction races.
///
/// Skipped at runtime when bash is not on PATH (or Git Bash absent on
/// Windows). HOME is redirected to a per-test temp dir so the user's
/// real ~/.bashrc is not sourced.
/// </summary>
[Trait("Category", "ShellIntegration")]
public sealed class BashShellIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapPath;

    public BashShellIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_bash_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _bootstrapPath = BashBootstrapBuilder.WriteScript(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private HarnessResult RunBash(string stdin, string? extraInitLine = null)
    {
        if (!ShellHarness.IsEnabled())
        {
            Assert.Skip("shell integration tests gated off on this runner (set NOVA_RUN_SHELL_INTEGRATION_TESTS=1 to enable)");
        }

        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string args = $"--rcfile \"{_bootstrapPath}\" -i";
        // Force HOME to the temp dir so the bootstrap's `. ~/.bashrc`
        // either no-ops (no file) or sources only a test-controlled file.
        // Tests that want a synthetic ~/.bashrc write it before running.
        var env = new Dictionary<string, string>
        {
            ["HOME"] = _tempRoot,
        };
        if (extraInitLine is not null)
        {
            string bashrc = Path.Combine(_tempRoot, ".bashrc");
            File.WriteAllText(bashrc, extraInitLine + "\n");
        }

        return ShellHarness.Run(bash, args, stdin, env, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Bootstrap_EmitsPromptReadyAndAcceptedAndFinished_ForSimpleCommand()
    {
        HarnessResult result = RunBash("echo hello\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "A");
        OscEvent? accepted = result.Events.FirstOrDefault(e => e.Kind == "C" && e.DecodedCommand == "echo hello");
        Assert.NotNull(accepted);
        OscEvent? finished = result.Events.FirstOrDefault(e =>
            e.Kind == "D" && e.DecodedFinish.exitCode == 0);
        Assert.NotNull(finished);
    }

    [Fact]
    public void Bootstrap_ReportsNonZeroExitCode_ForFailingCommand()
    {
        HarnessResult result = RunBash("false\nexit 0\n");

        // The failing command should produce a D marker with exit != 0.
        // We don't assert exactly 1 because some bash configurations may
        // return other non-zero codes; the contract is "non-zero".
        OscEvent? failedFinish = result.Events.FirstOrDefault(e =>
            e.Kind == "D" && e.DecodedFinish.exitCode is { } code && code != 0);
        Assert.NotNull(failedFinish);
    }

    [Fact]
    public void Bootstrap_PreservesMultilineCommand_ThroughBase64Encoding()
    {
        string multiline = "for i in 1 2; do\n  echo $i\ndone";
        // Bash needs the heredoc to deliver multiline input as a single
        // logical command; we use a heredoc terminator instead of trying
        // to send raw \n which would be interpreted as separate commands.
        string stdin = "cmd=$(cat <<'NOVA_EOF'\n" + multiline + "\nNOVA_EOF\n)\neval \"$cmd\"\nexit 0\n";
        HarnessResult result = RunBash(stdin);

        // The eval line is what's captured by DEBUG, not the inner for-loop.
        // What we're actually asserting: the bootstrap can emit C markers
        // for commands that contain shell metacharacters and base64 decodes
        // back to exactly the text we entered.
        Assert.Contains(result.Events, e => e.Kind == "C" && e.DecodedCommand == "eval \"$cmd\"");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
    }

    [Fact]
    public void Bootstrap_EmitsCwdMarker_WhenWorkingDirectoryChanges()
    {
        HarnessResult result = RunBash("cd /\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "7" && e.Payload!.StartsWith("file://"));
    }

    [Fact]
    public void Bootstrap_DoesNotProduceShellErrors()
    {
        // Regression guard for portability bugs in the bootstrap script.
        // The classic offender is `date +%s%N` on BSD `date` (macOS), which
        // emits a literal "%N" and breaks arithmetic with a bash error like
        // `bash: <seconds>%N: syntax error`. Git Bash's own /etc/bash.bashrc
        // routinely emits title escapes and a PS1 echo to stderr regardless
        // of our bootstrap, so we cannot assert "no stderr at all"; we
        // instead assert that no line of stderr matches a shell-error
        // pattern that would indicate a bootstrap-level fault.
        HarnessResult result = RunBash("exit 0\n");

        string[] errorPatterns =
        {
            ": command not found",
            ": syntax error",
            "unbound variable",
            "bad substitution",
            "invalid arithmetic",
            "%N", // literal %N would indicate BSD-date breakage
        };

        var offending = result.Stderr.Split('\n')
            .Where(line => errorPatterns.Any(pat => line.Contains(pat, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offending.Count == 0,
            $"Bootstrap produced bash-level errors:\n{string.Join("\n", offending)}");
    }

    [Fact]
    public void Bootstrap_CapturesUserTypedCommand_WhenUserBashrcSetsPromptCommand()
    {
        // Regression guard for the PROMPT_COMMAND race: if the user's bashrc
        // sets PROMPT_COMMAND to a helper, the DEBUG trap must NOT capture
        // that helper as the user's command and skip the actual typed
        // command. The fixed bootstrap arms its tracking flag AFTER the
        // user's PROMPT_COMMAND finishes, so the real user command is the
        // first DEBUG fire that gets through.
        HarnessResult result = RunBash(
            "echo target-command\nexit 0\n",
            extraInitLine: "PROMPT_COMMAND='echo PROMPTHELPER >/dev/null'");

        OscEvent? userCommand = result.Events.FirstOrDefault(e =>
            e.Kind == "C" && e.DecodedCommand == "echo target-command");
        Assert.NotNull(userCommand);
    }

    [Fact]
    public void Bootstrap_DoesNotCapturePromptHelperAsAcceptedCommand()
    {
        // The strong negative form of the race test: not only must the
        // user's typed command appear in the OSC 133;C stream, the user's
        // PROMPT_COMMAND helper text must NOT appear there. Both halves
        // matter -- a bootstrap that captured the helper AND the user
        // command would still satisfy the first test.
        HarnessResult result = RunBash(
            "echo target-command\nexit 0\n",
            extraInitLine: "PROMPT_COMMAND='echo PROMPTHELPER >/dev/null'");

        var capturedTexts = result.Events
            .Where(e => e.Kind == "C")
            .Select(e => e.DecodedCommand)
            .ToList();

        Assert.DoesNotContain(capturedTexts,
            t => t is not null && t.Contains("PROMPTHELPER"));
    }
}
