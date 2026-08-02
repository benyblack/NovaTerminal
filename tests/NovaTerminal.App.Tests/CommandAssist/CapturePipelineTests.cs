using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// Capture rules in isolation: which submissions become history entries, how the heuristic and
/// structured paths avoid writing the same command twice, and how the exit code lands afterwards.
/// </summary>
public sealed class CapturePipelineTests
{
    private static readonly DateTimeOffset EventTime = DateTimeOffset.Parse("2026-03-09T12:00:00+00:00");

    [Fact]
    public async Task CaptureSubmissionAsync_PersistsARedactedHeuristicEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.UpdateSession("pwsh", @"C:\repo", "profile-1", "session-1", "host-1", isRemote: true, isShellIntegrated: false);

        await pipeline.CaptureSubmissionAsync("  gh auth login --password hunter2  ", isSubmissionSuppressed: false);

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", entry.CommandText);
        Assert.True(entry.IsRedacted);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal("pwsh", entry.ShellKind);
        Assert.Equal(@"C:\repo", entry.WorkingDirectory);
        Assert.Equal("profile-1", entry.ProfileId);
        Assert.Equal("session-1", entry.SessionId);
        Assert.Equal("host-1", entry.HostId);
        Assert.True(entry.IsRemote);
        Assert.Null(entry.ExitCode);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhenShellKindIsUnknown_TagsTheEntryUnknown()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Equal("unknown", Assert.Single(store.Entries).ShellKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("echo one\necho two")]
    [InlineData("echo one\r\necho two")]
    public async Task CaptureSubmissionAsync_RejectsEmptyAndMultiLineSubmissions(string submission)
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CaptureSubmissionAsync(submission, isSubmissionSuppressed: false);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhenTheSubmissionIsSuppressed_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: true);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhileTheAltScreenIsUp_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetAltScreenActive(true);

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_OnceStructuredCaptureIsProven_StandsDown()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        context.ObserveStructuredCommandCaptureMarker();

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task CaptureSubmissionAsync_WhenTheStoreThrows_DoesNotPropagate()
    {
        var pipeline = new CapturePipeline(new ThrowingHistoryStore(), new SecretsFilter(), new AssistSessionContext());

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
    }

    [Fact]
    public async Task CompleteSubmissionAsync_PatchesTheExitCodeOfThePendingEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        await pipeline.CompleteSubmissionAsync(23);

        Assert.Equal(23, Assert.Single(store.Entries).ExitCode);
    }

    [Fact]
    public async Task CompleteSubmissionAsync_WithNothingPending_PatchesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.CompleteSubmissionAsync(1);

        Assert.Empty(store.PatchedEntryIds);
    }

    [Fact]
    public async Task CompleteSubmissionAsync_ConsumesThePendingEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        await pipeline.CompleteSubmissionAsync(0);
        await pipeline.CompleteSubmissionAsync(1);

        Assert.Single(store.PatchedEntryIds);
        Assert.Equal(0, Assert.Single(store.Entries).ExitCode);
    }

    [Fact]
    public async Task CommandAccepted_PersistsARedactedStructuredEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("gh auth login --password hunter2", @"C:\from-event"));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("gh auth login --password [REDACTED]", entry.CommandText);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.Equal(EventTime, entry.ExecutedAt);
        Assert.Equal(@"C:\from-event", entry.WorkingDirectory);
    }

    /// <summary>
    /// V2 Phase 2b inverted this case. It used to assert "integration disabled means no structured
    /// capture", where "disabled" meant only "we did not inject a bootstrap" - which is permanently
    /// true of every SSH session, and would have made the remote snippets useless.
    /// </summary>
    /// <remarks>
    /// The event is its own evidence. An accepted-command event can only reach here from an armed
    /// <c>ShellLifecycleTracker</c>, and a tracker only ever fires from a parser mark callback: if
    /// this method is running, a shell emitted <c>OSC 133;C</c>. Who installed the thing that emitted
    /// it is not a fact the capture path needs.
    /// </remarks>
    [Fact]
    public async Task CommandAccepted_FromAShellWeDidNotInstrument_IsStillCaptured()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.UpdateSession("bash", "/home/u", "p", "s", "remote-host", isRemote: true, isShellIntegrated: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("git status", entry.CommandText);
        Assert.Equal(CommandCaptureSource.ShellIntegration, entry.Source);
        Assert.True(entry.IsRemote);
        Assert.Equal("remote-host", entry.HostId);
        Assert.True(context.IsShellIntegrationLive);
    }

    [Fact]
    public async Task CommandAccepted_WhileTheAltScreenIsUp_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        context.SetAltScreenActive(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        Assert.Empty(store.Entries);
    }

    // ---- the textless C (V2 Phase 2b) ---------------------------------------------------------
    //
    // A `133;C` with no payload is legal FinalTerm and is what iTerm2's and VS Code's snippets
    // send. Phase 2b made the parser raise CommandAccepted for it with null text, so these four
    // tests are the contract for what the capture pipeline does with a lifecycle edge that carries
    // no command: everything except write an entry.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CommandAccepted_WithNoCommandText_CapturesNothing(string? commandText)
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted(commandText));

        Assert.Empty(store.Entries);
    }

    /// <summary>
    /// It still proves the session is instrumented - that is the whole reason the parser raises it -
    /// but it must not prove the shell <em>reports command text</em>.
    /// </summary>
    /// <remarks>
    /// The distinction is the difference between a working session and a silent one. Standing the
    /// heuristic path down is what <c>HasObservedStructuredCommandCaptureMarker</c> does, and a shell
    /// that emits a bare C forever would then have the heuristic path off and the structured path
    /// producing nothing: no history at all, from a session that looks fully integrated.
    /// </remarks>
    [Fact]
    public async Task CommandAccepted_WithNoCommandText_ProvesInstrumentationButNotStructuredCapture()
    {
        (CapturePipeline pipeline, _, AssistSessionContext context) = CreatePipeline();

        await pipeline.HandleShellIntegrationEventAsync(Accepted(null));

        Assert.True(context.HasObservedShellIntegrationMarker);
        Assert.True(context.IsShellIntegrationLive);
        Assert.False(context.HasObservedStructuredCommandCaptureMarker);
        Assert.False(context.IsStructuredCaptureActive);
    }

    /// <summary>The consequence of the flag above: Enter-time capture keeps working.</summary>
    [Fact]
    public async Task CommandAccepted_WithNoCommandText_LeavesTheHeuristicPathRunning()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted(null));

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);

        Assert.Equal(CommandCaptureSource.Heuristic, Assert.Single(store.Entries).Source);
    }

    /// <summary>
    /// The full bare-C cycle in order, which is the shape a third-party remote integration produces:
    /// the grid/heuristic path writes the entry at Enter, C arrives with nothing to add, and D
    /// patches the exit code and duration onto the entry that already exists.
    /// </summary>
    /// <remarks>
    /// The load-bearing part is that the textless C returns <em>before</em> touching the pending
    /// entry. Clearing it, or writing a second entry beside it, would leave D with nothing to patch
    /// and the command permanently recorded as still running.
    /// </remarks>
    [Fact]
    public async Task BareCCycle_CapturesOnceAndStillGetsItsExitCode()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.CaptureSubmissionAsync("dotnet test", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted(null));
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 3, duration: TimeSpan.FromSeconds(2)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal("dotnet test", entry.CommandText);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal(3, entry.ExitCode);
        Assert.Equal(2000, entry.DurationMs);
    }

    /// <summary>
    /// The first command of an instrumented session goes down both paths: the heuristic fires on
    /// Enter, and only then does the shell prove it reports command text. One entry must survive.
    /// </summary>
    [Fact]
    public async Task FirstCommandOfAnInstrumentedSession_IsCapturedOnceAndPatchedOnce()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Finished(exitCode: 0, duration: TimeSpan.FromSeconds(1)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(CommandCaptureSource.Heuristic, entry.Source);
        Assert.Equal(0, entry.ExitCode);
        Assert.Equal(1000, entry.DurationMs);
    }

    [Fact]
    public async Task SecondCommandOfAnInstrumentedSession_IsCapturedByTheStructuredPathOnly()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("git status", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));
        await pipeline.HandleShellIntegrationEventAsync(Finished(0, TimeSpan.FromSeconds(1)));

        // The marker is now proven, so the heuristic stands down for everything that follows.
        await pipeline.CaptureSubmissionAsync("dotnet test", isSubmissionSuppressed: false);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("dotnet test"));

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(CommandCaptureSource.ShellIntegration, store.Entries[1].Source);
    }

    [Fact]
    public async Task CommandAccepted_WhenTextDiffersFromThePendingHeuristicEntry_CapturesBoth()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("git stat", isSubmissionSuppressed: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(CommandCaptureSource.Heuristic, store.Entries[0].Source);
        Assert.Equal(CommandCaptureSource.ShellIntegration, store.Entries[1].Source);
    }

    [Fact]
    public async Task CommandAccepted_DedupIgnoresSurroundingWhitespace()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.CaptureSubmissionAsync("  git status  ", isSubmissionSuppressed: false);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status  "));

        Assert.Single(store.Entries);
    }

    [Fact]
    public async Task CommandFinished_PatchesExitCodeAndRoundedDuration()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        await pipeline.HandleShellIntegrationEventAsync(Finished(7, TimeSpan.FromMilliseconds(2500.6)));

        CommandHistoryEntry entry = Assert.Single(store.Entries);
        Assert.Equal(7, entry.ExitCode);
        Assert.Equal(2501, entry.DurationMs);
    }

    [Fact]
    public async Task CommandFinished_WithoutAnAcceptedCommand_PatchesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Finished(1, TimeSpan.FromMilliseconds(500)));

        Assert.Empty(store.Entries);
        Assert.Empty(store.PatchedEntryIds);
    }

    [Fact]
    public async Task CommandFinished_ConsumesThePendingEntry()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);
        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        await pipeline.HandleShellIntegrationEventAsync(Finished(0, TimeSpan.FromSeconds(1)));
        await pipeline.HandleShellIntegrationEventAsync(Finished(9, TimeSpan.FromSeconds(2)));

        Assert.Single(store.PatchedEntryIds);
        Assert.Equal(0, Assert.Single(store.Entries).ExitCode);
    }

    [Theory]
    [InlineData(ShellIntegrationEventType.PromptReady, false)]
    [InlineData(ShellIntegrationEventType.CommandStarted, false)]
    [InlineData(ShellIntegrationEventType.CommandFinished, false)]
    [InlineData(ShellIntegrationEventType.CommandAccepted, true)]
    public async Task ObservedMarkers_OnlyCommandAcceptedProvesStructuredCapture(
        ShellIntegrationEventType type,
        bool expectsStructuredCapture)
    {
        (CapturePipeline pipeline, _, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: type,
            Timestamp: EventTime,
            CommandText: "git status",
            WorkingDirectory: null,
            ExitCode: null,
            Duration: null));

        Assert.True(context.HasObservedShellIntegrationMarker);
        Assert.Equal(expectsStructuredCapture, context.HasObservedStructuredCommandCaptureMarker);
        Assert.Equal(expectsStructuredCapture, context.IsStructuredCaptureActive);
    }

    [Fact]
    public async Task WorkingDirectoryChanged_UpdatesTheContextWithoutClaimingAMarker()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();

        await pipeline.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.WorkingDirectoryChanged,
            Timestamp: EventTime,
            CommandText: null,
            WorkingDirectory: "/new/place",
            ExitCode: null,
            Duration: null));

        Assert.Equal("/new/place", context.WorkingDirectory);
        Assert.False(context.HasObservedShellIntegrationMarker);
        Assert.Empty(store.Entries);
    }

    private static (CapturePipeline Pipeline, InMemoryHistoryStore Store, AssistSessionContext Context) CreatePipeline()
    {
        var store = new InMemoryHistoryStore();
        var context = new AssistSessionContext();
        return (new CapturePipeline(store, new SecretsFilter(), context), store, context);
    }

    private static ShellIntegrationEvent Accepted(string? commandText, string? workingDirectory = null)
    {
        return new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandAccepted,
            Timestamp: EventTime,
            CommandText: commandText,
            WorkingDirectory: workingDirectory,
            ExitCode: null,
            Duration: null);
    }

    private static ShellIntegrationEvent Finished(int? exitCode, TimeSpan? duration)
    {
        return new ShellIntegrationEvent(
            Type: ShellIntegrationEventType.CommandFinished,
            Timestamp: EventTime.AddSeconds(1),
            CommandText: null,
            WorkingDirectory: null,
            ExitCode: exitCode,
            Duration: duration);
    }

    private sealed class InMemoryHistoryStore : IHistoryStore
    {
        private readonly List<CommandHistoryEntry> _entries = new();
        private readonly List<string> _patchedEntryIds = new();

        public IReadOnlyList<CommandHistoryEntry> Entries => _entries;
        public IReadOnlyList<string> PatchedEntryIds => _patchedEntryIds;

        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(_entries.Take(maxResults).ToList());

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(_entries.Take(maxCandidates).ToList());

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
        {
            int index = _entries.FindIndex(x => x.Id == entryId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _patchedEntryIds.Add(entryId);
            _entries[index] = _entries[index] with
            {
                ExitCode = exitCode,
                DurationMs = durationMs ?? _entries[index].DurationMs
            };
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingHistoryStore : IHistoryStore
    {
        public Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("simulated write failure"));

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(Array.Empty<CommandHistoryEntry>());

        public Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandHistoryEntry>>(Array.Empty<CommandHistoryEntry>());

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new InvalidOperationException("simulated write failure"));
    }
}
