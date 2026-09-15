using NovaTerminal.CommandAssist.ShellIntegration.Remote;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// The shipped bash/zsh snippet, run through a real bash on a real PTY.
/// </summary>
/// <remarks>
/// <para>
/// <c>RemoteShellIntegrationSnippetTests</c> asserts on the snippet's text, which is the right
/// instrument for "the guard we argued for is still there" and the wrong one for everything else:
/// the two bugs this file was written for - a phantom command captured on an empty Enter, and
/// <c>true &amp;&amp; false</c> recorded as <c>true</c> - are both invisible to a substring check and
/// both fell out of running the file in under a minute. Static checks stay; this is the layer that
/// can say the snippet <em>works</em>.
/// </para>
/// <para>
/// Installed the way a user installs it: a small rc file that sources the snippet, exactly like the
/// <c>. ~/.nova-shell-integration.sh</c> line the docs tell them to add to <c>~/.bashrc</c>. Not
/// passed as <c>--rcfile</c> directly, because that would test a shape nobody runs.
/// </para>
/// <para>
/// Skipped when bash is absent (no Git Bash on a Windows runner). <c>HOME</c> is redirected to a
/// per-test temp dir so the developer's own <c>~/.bashrc</c> is never sourced.
/// </para>
/// </remarks>
[Trait("Category", "ShellIntegration")]
[Collection(nameof(ShellIntegrationCollection))]
public sealed class RemoteBashSnippetIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _snippetPath;

    public RemoteBashSnippetIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_snippet_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _snippetPath = Path.Combine(_tempRoot, "nova-shell-integration.sh");
        File.WriteAllText(
            _snippetPath,
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    /// <param name="userRcLines">
    /// What the user's own <c>~/.bashrc</c> contains <em>before</em> the loader line, which is where
    /// a competing <c>PROMPT_COMMAND</c> comes from.
    /// </param>
    private HarnessResult RunSnippet(string stdin, string? userRcLines = null, bool sourceTwice = false)
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string loader = $". \"{_snippetPath.Replace('\\', '/')}\"";
        string rc = Path.Combine(_tempRoot, "rc");
        File.WriteAllText(
            rc,
            "PS1='nova-test$ '\n" +
            (userRcLines is null ? string.Empty : userRcLines + "\n") +
            loader + "\n" +
            (sourceTwice ? loader + "\n" : string.Empty),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var env = new Dictionary<string, string> { ["HOME"] = _tempRoot };
        return ShellHarness.Run(bash, $"--rcfile \"{rc}\" -i", stdin, env, TimeSpan.FromSeconds(20));
    }

    private static IReadOnlyList<string?> CapturedCommands(HarnessResult result) =>
        result.Events.Where(e => e.Kind == "C").Select(e => e.DecodedCommand).ToList();

    // ---- the happy path -------------------------------------------------------------------------

    [Fact]
    public void Snippet_EmitsTheFullLifecycle_ForASimpleCommand()
    {
        HarnessResult result = RunSnippet("echo hello\nexit 0\n");

        Assert.Contains(result.Events, e => e.Kind == "A");
        Assert.Contains(result.Events, e => e.Kind == "B");
        Assert.Contains(CapturedCommands(result), t => t == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
        Assert.Contains(result.Events, e => e.Kind == "7" && e.Payload!.StartsWith("file://"));
    }

    [Fact]
    public void Snippet_EmitsThePromptEndMarkAtTheFirstCellOfInput()
    {
        HarnessResult result = RunSnippet("exit 0\n");

        var marks = result.Events.Where(e => e.Kind == "B").ToList();
        Assert.NotEmpty(marks);
        Assert.Contains(marks, m => m.MarkPosition is { } p && p.column == "nova-test$ ".Length);
    }

    [Fact]
    public void Snippet_ProducesNoShellErrors()
    {
        HarnessResult result = RunSnippet("exit 0\n");

        string[] errorPatterns =
        {
            ": command not found",
            ": syntax error",
            "unbound variable",
            "bad substitution",
            "invalid arithmetic",
            "%N",
        };

        var offending = result.Stderr.Split('\n')
            .Where(line => errorPatterns.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offending.Count == 0, $"snippet produced bash-level errors:\n{string.Join("\n", offending)}");
    }

    [Fact]
    public void Snippet_SourcedTwice_DoesNotDoubleTheMarks()
    {
        HarnessResult result = RunSnippet("echo hello\nexit 0\n", sourceTwice: true);

        Assert.Equal(1, CapturedCommands(result).Count(t => t == "echo hello"));
    }

    // ---- the phantom command (PR #289 review, B2) -----------------------------------------------

    /// <summary>
    /// Pressing Enter on an empty line must capture nothing.
    /// </summary>
    /// <remarks>
    /// bash runs <c>PROMPT_COMMAND</c> after an empty Enter too, and on that path no user command
    /// ran - so nothing had raised the DEBUG-trap busy flag, and the first entry of the user's own
    /// <c>PROMPT_COMMAND</c> chain became the "accepted command". A user with any prompt framework
    /// installed got that framework's hook name written to permanent history every time they hit
    /// Enter at an empty prompt. Fixed by raising the flag as <c>__nova_precmd</c>'s first act,
    /// which restores the busy-for-the-whole-chain invariant the design always claimed.
    /// </remarks>
    [Fact]
    public void Snippet_OnEmptyEnterWithAUserPromptCommandHook_CapturesNothing()
    {
        HarnessResult result = RunSnippet(
            "\nexit 0\n",
            userRcLines: "__user_hook() { :; }\nPROMPT_COMMAND='__user_hook'");

        IReadOnlyList<string?> captured = CapturedCommands(result);

        Assert.DoesNotContain(captured, t => t is not null && t.Contains("__user_hook", StringComparison.Ordinal));

        // Stronger: the only command this session ran is the one that ended it.
        Assert.Equal(new string?[] { "exit 0" }, captured);
    }

    /// <summary>
    /// The same for several empty Enters in a row - the flag has to be re-raised every cycle, not
    /// once.
    /// </summary>
    [Fact]
    public void Snippet_OnRepeatedEmptyEnters_CapturesNothing()
    {
        HarnessResult result = RunSnippet(
            "\n\n\nexit 0\n",
            userRcLines: "__user_hook() { :; }\nPROMPT_COMMAND='__user_hook'");

        Assert.Equal(new string?[] { "exit 0" }, CapturedCommands(result));
    }

    /// <summary>
    /// And the positive half: a user <c>PROMPT_COMMAND</c> must not cost the real command either.
    /// </summary>
    [Fact]
    public void Snippet_WithAUserPromptCommandHook_StillCapturesTheTypedCommand()
    {
        HarnessResult result = RunSnippet(
            "echo target-command\nexit 0\n",
            userRcLines: "__user_hook() { :; }\nPROMPT_COMMAND='__user_hook'");

        IReadOnlyList<string?> captured = CapturedCommands(result);
        Assert.Contains(captured, t => t == "echo target-command");
        Assert.DoesNotContain(captured, t => t is not null && t.Contains("__user_hook", StringComparison.Ordinal));
    }

    // ---- the whole line, not the first simple command (PR #289 review, B3) -----------------------

    /// <summary>
    /// <c>$BASH_COMMAND</c> in a DEBUG trap is the first <em>simple command</em>, so the snippet used
    /// to record <c>true</c> for <c>true &amp;&amp; false</c> - the wrong text, and next to the
    /// other branch's exit code. Reading the line back out of <c>history 1</c> is what fixes it.
    /// </summary>
    [Theory]
    [InlineData("true && false", 1)]
    [InlineData("false || true", 0)]
    [InlineData("true; false", 1)]
    public void Snippet_CapturesTheWholeLine_NotTheFirstSimpleCommand(string line, int expectedExit)
    {
        HarnessResult result = RunSnippet(line + "\nexit 0\n");

        Assert.Contains(CapturedCommands(result), t => t == line);
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == expectedExit);
    }

    [Fact]
    public void Snippet_CapturesAPipeline_AsTheWholeLine()
    {
        HarnessResult result = RunSnippet("echo one | cat | cat\nexit 0\n");

        Assert.Contains(CapturedCommands(result), t => t == "echo one | cat | cat");
    }

    // ---- commands the name filter used to eat (PR #289 review, N7) ------------------------------

    /// <summary>
    /// The DEBUG-trap filter used to skip anything whose first word began with <c>trap</c> or
    /// <c>PROMPT_COMMAND</c>, which silently dropped real user commands. The busy-flag invariant is
    /// what keeps our own hooks out; the name patterns were unnecessary, and only <c>__nova_*</c>
    /// remains.
    /// </summary>
    [Theory]
    [InlineData("trap-something --help")]
    [InlineData("PROMPT_COMMANDS_LIST=1 echo x")]
    public void Snippet_CapturesCommandsTheOldNameFilterWouldHaveDropped(string line)
    {
        HarnessResult result = RunSnippet(line + " 2>/dev/null\nexit 0\n");

        Assert.Contains(CapturedCommands(result), t => t == line + " 2>/dev/null");
    }

    /// <summary>The other side of dropping those patterns: our own hooks still never appear.</summary>
    [Fact]
    public void Snippet_NeverCapturesItsOwnHooks()
    {
        HarnessResult result = RunSnippet(
            "echo hello\ntrue\nexit 0\n",
            userRcLines: "__user_hook() { :; }\nPROMPT_COMMAND='__user_hook'");

        Assert.DoesNotContain(
            CapturedCommands(result),
            t => t is not null && t.Contains("__nova_", StringComparison.Ordinal));
    }
}
