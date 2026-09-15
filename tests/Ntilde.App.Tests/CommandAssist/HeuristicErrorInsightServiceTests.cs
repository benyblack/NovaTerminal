using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The service that drives the recogniser table: dispatch, ordering, and the ladder of how much it
/// is willing to infer from how little it can see.
/// </summary>
/// <remarks>
/// The individual failure classes are covered in <see cref="CommandErrorRecognizerTests"/>, against
/// messages captured from real runs. What is pinned here is everything that is <em>not</em> a
/// recogniser: the fallbacks when no recogniser matches, the confidence ceiling those fallbacks
/// obey, and the ordering the popup's selected row depends on.
/// </remarks>
public sealed class HeuristicErrorInsightServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_WhenCommandNotFound_ReturnsHighConfidenceFix()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "gti status", 127, "bash", "command not found: gti");

        Assert.Contains(result, item => item.Confidence >= 0.8);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenPowerShellCommandNotRecognized_SuggestsLikelyCommand()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "Get-ChldItem", 1, "pwsh", "The term 'Get-ChldItem' is not recognized");

        Assert.Contains(result, item => item.SuggestedCommand.Contains("Get-ChildItem", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_WhenWindowsCommandIsNotRecognized_SuggestsLikelyCommand()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "gti status", 1, "cmd", "'gti' is not recognized as an internal or external command");

        Assert.Contains(result, item => item.SuggestedCommand.StartsWith("git ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_WhenNoSuchFileOrDirectory_SuggestsCurrentDirectoryInvocation()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "build.sh", 127, "bash", "No such file or directory");

        Assert.Contains(result, item => item.SuggestedCommand == "./build.sh");
    }

    [Fact]
    public async Task AnalyzeAsync_WhenFailureIsLowConfidence_DoesNotReturnAutoOpenWorthySuggestion()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "invoke-something --mystery", 1, "pwsh", "operation failed");

        Assert.DoesNotContain(result, item => item.Confidence >= 0.8);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenCommandAlreadyMatchesKnownToken_DoesNotReturnTypoFix()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze("git commit", 1, "bash", null);

        Assert.DoesNotContain(result, item => item.Badges?.Contains("Typo", StringComparer.Ordinal) == true);
    }

    // ---------------------------------------------------------------- the output-tail ladder

    /// <summary>
    /// Rung 3: nothing was captured. This is where V1 lived permanently, and the old answer - a
    /// name-similarity guess - is still the only one available, so it survives. What it may not do
    /// is open a popup on the strength of it.
    /// </summary>
    [Fact]
    public async Task WithNoCapturedOutput_ATypoIsStillOfferedButCannotAutoOpen()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze("dokcer ps", 1, "bash", outputTail: null);

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal("docker ps", suggestion.SuggestedCommand);
        Assert.True(suggestion.Confidence < 0.8, "a guess made with no evidence must not interrupt");
        Assert.Contains("No output was captured", suggestion.Description!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rung 2, and the correction of V1's actual bug. With output in hand that says nothing about
    /// the name being unresolvable, "did you mean docker?" is not a fix - the command ran. It drops
    /// to the bottom of the confidence range rather than being published at 0.82 next to a
    /// threshold of 0.8.
    /// </summary>
    [Fact]
    public async Task WithCapturedOutputThatMatchesNothing_ATypoGuessIsDemotedFurther()
    {
        IReadOnlyList<CommandFixSuggestion> withoutOutput = await Analyze("dokcer ps", 1, "bash", null);
        IReadOnlyList<CommandFixSuggestion> withOutput = await Analyze(
            "dokcer ps", 1, "bash", "Ambiguous option: --frobnicate");

        Assert.True(
            Assert.Single(withOutput).Confidence < Assert.Single(withoutOutput).Confidence,
            "seeing output that explains nothing is weaker evidence than seeing none at all");
    }

    /// <summary>Rung 1: a recogniser matched, so no fallback is added on top of it.</summary>
    [Fact]
    public async Task WhenARecognizerMatches_TheUninformedFallbackIsNotAdded()
    {
        IReadOnlyList<CommandFixSuggestion> result = await Analyze(
            "git status", 128, "bash", "fatal: not a git repository (or any of the parent directories): .git");

        Assert.DoesNotContain(result, item => item.Description?.Contains("No output was captured", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AZeroExitCodeProducesNothing()
    {
        Assert.Empty(await Analyze("dokcer ps", 0, "bash", null));
    }

    // ---------------------------------------------------------------- dispatch

    /// <summary>
    /// The popup selects row 0 and the bubble shows it, so the order has to be by confidence and
    /// has to be stable - two runs of the same failure must not swap the top two rows.
    /// </summary>
    [Fact]
    public async Task SuggestionsAreOrderedByConfidenceAndAreStable()
    {
        IReadOnlyList<CommandFixSuggestion> first = await Analyze(
            "./deploy.sh", 126, "bash", "bash: ./deploy.sh: Permission denied");
        IReadOnlyList<CommandFixSuggestion> second = await Analyze(
            "./deploy.sh", 126, "bash", "bash: ./deploy.sh: Permission denied");

        Assert.True(first.Count > 1);
        Assert.Equal(
            first.Select(item => item.SuggestedCommand),
            second.Select(item => item.SuggestedCommand));

        for (int i = 1; i < first.Count; i++)
        {
            Assert.True(first[i - 1].Confidence >= first[i].Confidence);
        }
    }

    /// <summary>
    /// Two recognisers can legitimately reach the same command from different evidence; the surface
    /// must not show it twice, and the stronger claim wins.
    /// </summary>
    [Fact]
    public async Task DuplicateSuggestedCommandsAreCollapsedKeepingTheStrongestClaim()
    {
        var table = new List<CommandErrorRecognizer>
        {
            new("weak", "weak", _ => [Suggestion("git status", 0.3)]),
            new("strong", "strong", _ => [Suggestion("git status", 0.9)]),
        };

        var service = new HeuristicErrorInsightService(table);
        IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(Context("gti status", 1, "bash", "x"));

        CommandFixSuggestion suggestion = Assert.Single(result);
        Assert.Equal(0.9, suggestion.Confidence);
    }

    [Fact]
    public async Task AnEmptyCommandProducesNothing()
    {
        Assert.Empty(await Analyze("   ", 1, "bash", "command not found"));
    }

    private static CommandFixSuggestion Suggestion(string command, double confidence)
        => new($"Try {command}", command, null, confidence, ["Fix"]);

    private static CommandFailureContext Context(
        string commandText, int exitCode, string shellKind, string? outputTail)
        => new(
            CommandText: commandText,
            ExitCode: exitCode,
            ShellKind: shellKind,
            WorkingDirectory: null,
            OutputTail: outputTail,
            IsRemote: false,
            SelectedText: null);

    private static Task<IReadOnlyList<CommandFixSuggestion>> Analyze(
        string commandText, int exitCode, string shellKind, string? outputTail)
        => new HeuristicErrorInsightService().AnalyzeAsync(
            Context(commandText, exitCode, shellKind, outputTail), CancellationToken.None);
}
