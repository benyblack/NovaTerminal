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

    [Fact]
    public async Task CommandAccepted_WhenShellIntegrationIsDisabled_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, _) = CreatePipeline();

        await pipeline.HandleShellIntegrationEventAsync(Accepted("git status"));

        Assert.Empty(store.Entries);
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

    [Fact]
    public async Task CommandAccepted_WithNoCommandText_CapturesNothing()
    {
        (CapturePipeline pipeline, InMemoryHistoryStore store, AssistSessionContext context) = CreatePipeline();
        context.SetShellIntegrationEnabled(true);

        await pipeline.HandleShellIntegrationEventAsync(Accepted("   "));

        Assert.Empty(store.Entries);
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
