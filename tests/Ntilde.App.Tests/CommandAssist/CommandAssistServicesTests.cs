using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The retention cap must never be expressed as a history-store swap.
/// </summary>
/// <remarks>
/// <c>JsonlHistoryStore</c> caches an index and a line count, and compaction rewrites the
/// whole file from that cache. Two live instances over one file therefore each rewrite from their
/// own stale view: the older one resurrects entries the newer one's cap deleted, and either can
/// drop the other's appends. The pre-Phase-0b <c>JsonHistoryStore</c> was stateless per operation,
/// which is the only reason swapping instances used to be survivable.
/// </remarks>
public sealed class CommandAssistServicesTests : IDisposable
{
    private readonly string _tempRoot;

    public CommandAssistServicesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_services_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ApplyHistoryRetentionLimit_NeverReplacesTheHistoryStore()
    {
        CommandAssistServices services = CreateServices(maxHistoryEntries: 50);
        IHistoryStore first = services.HistoryStore;

        services.ApplyHistoryRetentionLimit(1);
        services.ApplyHistoryRetentionLimit(5000);
        services.ApplyHistoryRetentionLimit(50);

        Assert.Same(first, services.HistoryStore);
    }

    /// <summary>
    /// The pane that changed the setting and the panes that captured the store earlier must see
    /// one history, which only holds if the cap lands on the instance they are all holding.
    /// </summary>
    [Fact]
    public async Task ApplyHistoryRetentionLimit_LowersTheCapOnTheStoreCallersAlreadyHold()
    {
        CommandAssistServices services = CreateServices(maxHistoryEntries: 50);
        IHistoryStore captured = services.HistoryStore;
        await captured.AppendAsync(CreateEntry("git status", "2026-03-01T10:00:00+00:00"));
        await captured.AppendAsync(CreateEntry("dotnet test", "2026-03-01T10:01:00+00:00"));

        services.ApplyHistoryRetentionLimit(1);

        CommandHistoryEntry kept = Assert.Single(await captured.GetRecentAsync(10));
        Assert.Equal("dotnet test", kept.CommandText);
    }

    /// <summary>Applying the cap before anything reads <c>HistoryStore</c> must not force it into existence.</summary>
    [Fact]
    public async Task ApplyHistoryRetentionLimit_BeforeFirstUse_IsHonoredByTheStoreItLaterBuilds()
    {
        CommandAssistServices services = CreateServices(maxHistoryEntries: 50);

        services.ApplyHistoryRetentionLimit(1);

        IHistoryStore store = services.HistoryStore;
        await store.AppendAsync(CreateEntry("git status", "2026-03-01T10:00:00+00:00"));
        await store.AppendAsync(CreateEntry("dotnet test", "2026-03-01T10:01:00+00:00"));

        CommandHistoryEntry kept = Assert.Single(await store.GetRecentAsync(10));
        Assert.Equal("dotnet test", kept.CommandText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private CommandAssistServices CreateServices(int maxHistoryEntries)
    {
        return new CommandAssistServices(
            Path.Combine(_tempRoot, "history.jsonl"),
            legacyHistoryFilePath: null,
            Path.Combine(_tempRoot, "snippets.json"),
            () => _tempRoot,
            maxHistoryEntries);
    }

    private static CommandHistoryEntry CreateEntry(string commandText, string executedAt)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: DateTimeOffset.Parse(executedAt),
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ProfileId: "profile-1",
            SessionId: "session-1",
            HostId: null,
            ExitCode: 0,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null);
    }
}
