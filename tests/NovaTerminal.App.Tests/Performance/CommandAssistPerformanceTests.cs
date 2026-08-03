using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using Xunit;

namespace NovaTerminal.Tests.Performance;

/// <summary>
/// Command Assist regression tripwires for the V2 spec's latency targets (design doc: &lt;16 ms first
/// suggestion paint, &lt;30 ms incremental refresh, no typing jank).
/// </summary>
/// <remarks>
/// <para>
/// <strong>These are tripwires, not benchmarks, and the difference matters.</strong> They run inside
/// the ordinary xUnit suite on whatever machine CI gives them, next to tests doing IO; there is no
/// warmup discipline beyond a few throwaway iterations, no isolation, and no statistical treatment
/// beyond a p95. The repo already has a real BenchmarkDotNet project
/// (<c>tests/NovaTerminal.Benchmarks</c>) and this is deliberately not it - a benchmark that CI does
/// not run is a benchmark nobody reads, and what Phase 3b needs is something that fails loudly when a
/// change makes the assist an order of magnitude slower.
/// </para>
/// <para>
/// So the thresholds are set generously above the spec figures, and the numbers are written to test
/// output for the PR body rather than asserted tightly. A failure here means "something got much
/// worse", not "we missed 16 ms".
/// </para>
/// <para>
/// What is <em>not</em> measured: rendering. The spec's "first suggestion paint" includes an Avalonia
/// layout and draw pass that a headless unit test cannot honestly time, so these cover everything up
/// to the view-model write and say so rather than claiming the whole number.
/// </para>
/// </remarks>
public sealed class CommandAssistPerformanceTests
{
    private const int HistorySize = 5000;

    private readonly ITestOutputHelper _output;

    public CommandAssistPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The ranking pass itself, over a history file at the default retention cap, ranked against a
    /// typical mid-typing query.
    /// </summary>
    /// <remarks>
    /// Deliberately harder than production: the store's recall gate hands the engine at most 200
    /// candidates (<c>SuggestionOrchestrator.HistoryCandidatePoolSize</c>), so ranking all 5000 is the
    /// pessimistic bound rather than the live path.
    /// </remarks>
    [Fact]
    [Trait("Category", "Latency")]
    public void RankingPass_OverAFullHistory_StaysUnderTheIncrementalRefreshBudget()
    {
        IReadOnlyList<CommandHistoryEntry> entries = BuildHistory(HistorySize);
        var engine = new CommandAssistSuggestionEngine(new NoPathSuggestionProvider());
        CommandAssistQueryContext context = BuildContext("git st");

        // Warmup: first call pays JIT for the engine, the comparer and the LINQ pipeline.
        for (int i = 0; i < 3; i++)
        {
            engine.GetSuggestions(entries, context, SuggestionCap);
        }

        var samples = new List<double>(Iterations);
        var stopwatch = new Stopwatch();
        for (int i = 0; i < Iterations; i++)
        {
            stopwatch.Restart();
            IReadOnlyList<AssistSuggestion> suggestions = engine.GetSuggestions(entries, context, SuggestionCap);
            stopwatch.Stop();

            Assert.NotEmpty(suggestions);
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        double p95 = Percentile(samples, 0.95);
        _output.WriteLine($"Ranking {HistorySize} entries: p50 {Percentile(samples, 0.5):F2}ms, p95 {p95:F2}ms, max {samples.Max():F2}ms");

        Assert.True(
            p95 < IncrementalRefreshBudgetMs,
            $"Ranking p95 was {p95:F2}ms, over the {IncrementalRefreshBudgetMs}ms tripwire.");
    }

    /// <summary>
    /// The empty-query pass, which is what <c>Ctrl+R</c> and <c>Ctrl+Space</c> run on an untouched
    /// prompt: every candidate is a match, so nothing is filtered out before scoring.
    /// </summary>
    [Fact]
    [Trait("Category", "Latency")]
    public void RankingPass_WithNoQuery_StaysUnderTheIncrementalRefreshBudget()
    {
        IReadOnlyList<CommandHistoryEntry> entries = BuildHistory(HistorySize);
        var engine = new CommandAssistSuggestionEngine(new NoPathSuggestionProvider());
        CommandAssistQueryContext context = BuildContext(string.Empty);

        for (int i = 0; i < 3; i++)
        {
            engine.GetSuggestions(entries, context, SuggestionCap);
        }

        var samples = new List<double>(Iterations);
        var stopwatch = new Stopwatch();
        for (int i = 0; i < Iterations; i++)
        {
            stopwatch.Restart();
            engine.GetSuggestions(entries, context, SuggestionCap);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        double p95 = Percentile(samples, 0.95);
        _output.WriteLine($"Ranking {HistorySize} entries, empty query: p50 {Percentile(samples, 0.5):F2}ms, p95 {p95:F2}ms");

        Assert.True(
            p95 < IncrementalRefreshBudgetMs,
            $"Empty-query ranking p95 was {p95:F2}ms, over the {IncrementalRefreshBudgetMs}ms tripwire.");
    }

    /// <summary>
    /// "No typing jank", measured where jank would come from: the keystroke path. Every
    /// <c>NotifyInputActivity</c> must return in microseconds and must not have started a ranking pass,
    /// because the debounce has not elapsed.
    /// </summary>
    /// <remarks>
    /// This is the debounce's tripwire. Disable the debounce and the recall count goes from one to one
    /// per keystroke, and the per-keystroke cost stops being a queue hop and becomes a store query plus
    /// a ranking pass over the whole recall pool.
    /// </remarks>
    [Fact]
    [Trait("Category", "Latency")]
    public async Task Typing_ABurstOfKeystrokes_CostsOneRecallAndNoBlockingWork()
    {
        var history = new CountingHistoryStore(BuildHistory(HistorySize));
        var grid = new FakeGrid();
        var gate = new GatedDelay();
        CommandAssistController controller = new(
            history,
            new SecretsFilter(),
            new CommandAssistSuggestionEngine(new NoPathSuggestionProvider()),
            snippetStore: null,
            commandDocsProvider: null,
            recipeProvider: null,
            errorInsightService: null,
            modeRouter: null,
            resultBuilder: null,
            queryProvider: grid.Read,
            renderedSurfaceProbe: null,
            dispatch: null,
            passiveRefreshDebounce: TimeSpan.FromMilliseconds(75),
            refreshDelay: gate.Delay);
        grid.OpenPrompt(controller);

        // Per-keystroke samples, not one division at the end (PR #293 review, non-blocking 9). A mean
        // over 200 keys hides exactly the regression that matters: one keystroke that blocks for 100 ms
        // moves the average by 0.5 ms and is invisible, while being precisely the jank the user feels.
        // p95 is the tail statistic, so a handful of slow keys fails the test rather than being averaged
        // away by the fast ones.
        const int keystrokes = 200;
        var samples = new List<double>(keystrokes);
        var stopwatch = new Stopwatch();
        for (int i = 0; i < keystrokes; i++)
        {
            grid.SetLine("git st" + i);
            stopwatch.Restart();
            controller.NotifyInputActivity();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        double p95 = Percentile(samples, 0.95);
        double mean = samples.Sum() / samples.Count;
        _output.WriteLine(
            $"Keystroke handling over {keystrokes} keys: mean {mean:F3}ms, p50 {Percentile(samples, 0.5):F3}ms, " +
            $"p95 {p95:F3}ms, max {samples.Max():F3}ms, {history.RecallCount} recalls while debouncing");

        Assert.Equal(0, history.RecallCount);
        Assert.True(
            p95 < KeystrokeP95BudgetMs,
            $"Keystroke handling p95 was {p95:F3}ms, over the {KeystrokeP95BudgetMs}ms tripwire.");

        gate.ReleaseAll();
        await WaitForAsync(() => history.RecallCount > 0);
        Assert.Equal(1, history.RecallCount);
    }

    /// <summary>
    /// End to end, with the debounce out of the way: from the keystroke to the bubble's content being
    /// set. Everything except the draw.
    /// </summary>
    [Fact]
    [Trait("Category", "Latency")]
    public async Task FirstSuggestion_FromKeystrokeToViewModel_StaysUnderTheFirstPaintBudget()
    {
        var history = new CountingHistoryStore(BuildHistory(HistorySize));
        var grid = new FakeGrid();
        CommandAssistController controller = new(
            history,
            new SecretsFilter(),
            new CommandAssistSuggestionEngine(new NoPathSuggestionProvider()),
            snippetStore: null,
            commandDocsProvider: null,
            recipeProvider: null,
            errorInsightService: null,
            modeRouter: null,
            resultBuilder: null,
            queryProvider: grid.Read,
            renderedSurfaceProbe: null,
            dispatch: null,
            passiveRefreshDebounce: TimeSpan.Zero);
        grid.OpenPrompt(controller);

        // Warmup pass: pays the JIT and the first Task.Run's thread-pool ramp.
        grid.SetLine("git st");
        controller.NotifyInputActivity();
        await WaitForAsync(() => controller.ViewModel.IsVisible);

        var samples = new List<double>(FirstPaintIterations);
        for (int i = 0; i < FirstPaintIterations; i++)
        {
            await controller.HandleEnterAsync(submittedText: null);
            grid.OpenPrompt(controller);
            grid.SetLine("git st");

            var stopwatch = Stopwatch.StartNew();
            controller.NotifyInputActivity();
            SpinUntil(() => controller.ViewModel.IsVisible, timeoutMs: 5000);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        double p95 = Percentile(samples, 0.95);
        _output.WriteLine($"Keystroke to view-model content: p50 {Percentile(samples, 0.5):F2}ms, p95 {p95:F2}ms (excludes rendering)");

        Assert.True(
            p95 < FirstSuggestionBudgetMs,
            $"First-suggestion p95 was {p95:F2}ms, over the {FirstSuggestionBudgetMs}ms tripwire.");
    }

    // ---------------------------------------------------------------- budgets

    /// <summary>
    /// Spec target 30 ms, kept at the spec figure. Measured p95 for ranking all 5000 entries is ~3 ms -
    /// an order of magnitude of headroom against a bound production never reaches, since the recall gate
    /// hands the engine 200 candidates - so there is no reason to inflate it.
    /// </summary>
    private const double IncrementalRefreshBudgetMs = 30;

    /// <summary>
    /// Spec target 16 ms for the paint. This measures the work behind it - a queue hop, a grid read, a
    /// store recall and a ranking pass, all on the thread pool - and allows 50 ms, because the async
    /// hops make this the one figure here that a busy CI agent can legitimately inflate without
    /// anything having regressed. Measured p95 is ~0.2 ms, so 50 ms is a tripwire rather than a target.
    /// </summary>
    private const double FirstSuggestionBudgetMs = 50;

    /// <summary>
    /// What a keystroke may cost on the caller's thread, at p95.
    /// </summary>
    /// <remarks>
    /// The path queues a task and returns, so the measured figure is a few microseconds and the mean was
    /// held at 1 ms. Moving to p95 (PR #293 review) makes the statistic tail-sensitive, and 5 ms is the
    /// budget that goes with it: a single sample can legitimately absorb a thread-pool wake or a gen-0
    /// collection on a shared CI agent, and it takes ten of 200 samples over the line to fail. Still two
    /// orders of magnitude above what the path costs when it is behaving, and comfortably inside a frame.
    /// </remarks>
    private const double KeystrokeP95BudgetMs = 5;

    private const int Iterations = 50;
    private const int FirstPaintIterations = 20;
    private const int SuggestionCap = 50;

    // ---------------------------------------------------------------- helpers

    private static double Percentile(List<double> samples, double percentile)
    {
        List<double> ordered = samples.OrderBy(sample => sample).ToList();
        int index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    /// <summary>
    /// Busy-waits for a condition, for the one measurement where the wait is inside the stopwatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Task.Delay(1)</c> is not 1 ms on Windows - the default timer quantum is ~15.6 ms - so a
    /// polling wait made the end-to-end figure read "15.5 ms" no matter what the assist did. That is a
    /// measurement of the clock, not of the code. Spinning costs a core for a few milliseconds twenty
    /// times and is the honest way to time this in-process.
    /// </para>
    /// <para>
    /// Safe here because the pass does not need this thread: the refresh runs on the thread pool and
    /// the controller's default dispatcher applies the outcome inline on that worker.
    /// </para>
    /// <para>
    /// <see cref="SpinWait.SpinOnce()"/> rather than <see cref="Thread.Yield"/> (PR #293 review,
    /// non-blocking 9). <c>Thread.Yield</c> is an unconditional syscall that gives up the rest of the
    /// quantum to a ready thread on the same core, so on a single-core CI agent this loop could hand the
    /// core to the very worker it is waiting for and then be scheduled back only after a full quantum -
    /// which lands in the measurement. <c>SpinWait</c> spins in user mode first and escalates to a yield
    /// or a sleep on its own schedule, which is both cheaper for a wait this short and the documented way
    /// to spin.
    /// </para>
    /// </remarks>
    private static void SpinUntil(Func<bool> predicate, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var spinner = new SpinWait();
        while (!predicate())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for the assist to settle.");
            }

            spinner.SpinOnce();
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for the assist to settle.");
            }

            await Task.Delay(1);
        }
    }

    /// <summary>
    /// A history file shaped like a real one: a small vocabulary of commands repeated across
    /// directories and exit codes, so the engine's cwd, recency and exit-code terms all have something
    /// to discriminate on.
    /// </summary>
    private static IReadOnlyList<CommandHistoryEntry> BuildHistory(int count)
    {
        string[] commands =
        {
            "git status", "git stash", "git commit -m \"wip\"", "git push", "git pull --rebase",
            "dotnet build", "dotnet test", "ls -la", "grep -rn TODO .", "cd ../src",
        };

        var entries = new List<CommandHistoryEntry>(count);
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(-30);
        for (int i = 0; i < count; i++)
        {
            entries.Add(new CommandHistoryEntry(
                Id: i.ToString(),
                CommandText: commands[i % commands.Length] + " " + i,
                ExecutedAt: start.AddSeconds(i),
                ShellKind: "pwsh",
                WorkingDirectory: i % 3 == 0 ? @"C:\repo" : @"C:\other",
                ProfileId: "profile-1",
                SessionId: "session-" + (i % 7),
                HostId: null,
                ExitCode: i % 11 == 0 ? 1 : 0,
                IsRemote: false,
                IsRedacted: false,
                Source: CommandCaptureSource.Heuristic,
                DurationMs: 12));
        }

        return entries;
    }

    private static CommandAssistQueryContext BuildContext(string query) => new(
        query,
        @"C:\repo",
        "pwsh",
        "profile-1",
        IsRemote: false,
        IncludeHistorySuggestions: true,
        IncludeSnippetSuggestions: false,
        IncludePathSuggestions: false,
        HostId: null);

    private sealed class NoPathSuggestionProvider : IPathSuggestionProvider
    {
        public IReadOnlyList<AssistSuggestion> GetSuggestions(CommandAssistQueryContext context, int maxResults)
            => Array.Empty<AssistSuggestion>();
    }

    private sealed class FakeGrid
    {
        private readonly object _gate = new();
        private AssistQuerySnapshot? _snapshot;

        public AssistQuerySnapshot? Read()
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }

        public void SetLine(string text)
        {
            lock (_gate)
            {
                _snapshot = new AssistQuerySnapshot(text, text.Length, IsMultiline: false, RightPromptTrimmed: false);
            }
        }

        public void OpenPrompt(CommandAssistController controller)
        {
            SetLine(string.Empty);
            controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
                Type: ShellIntegrationEventType.CommandStarted,
                Timestamp: DateTimeOffset.UtcNow,
                CommandText: null,
                WorkingDirectory: null,
                ExitCode: null,
                Duration: null)).GetAwaiter().GetResult();
        }
    }

    /// <summary>A read-only store over a fixed candidate set that counts how often it was asked.</summary>
    private sealed class CountingHistoryStore : IHistoryStore
    {
        private readonly IReadOnlyList<CommandHistoryEntry> _entries;
        private int _recallCount;

        public CountingHistoryStore(IReadOnlyList<CommandHistoryEntry> entries)
        {
            _entries = entries;
        }

        public int RecallCount => Volatile.Read(ref _recallCount);

        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _recallCount);
            return Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(_entries.Take(maxResults).ToArray());
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _recallCount);
            return Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(_entries.Take(maxCandidates).ToArray());
        }

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>Holds every debounced pass until the test releases it. See the passive-bubble suite.</summary>
    private sealed class GatedDelay
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = new();

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending.Add(completion);
            }

            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void ReleaseAll()
        {
            TaskCompletionSource[] pending;
            lock (_gate)
            {
                pending = _pending.ToArray();
                _pending.Clear();
            }

            foreach (TaskCompletionSource completion in pending)
            {
                completion.TrySetResult();
            }
        }
    }
}
