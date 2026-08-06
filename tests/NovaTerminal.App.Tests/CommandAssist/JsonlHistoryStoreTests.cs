using System.Text.Json;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class JsonlHistoryStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _historyPath;
    private readonly string _legacyPath;

    public JsonlHistoryStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_history_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _historyPath = Path.Combine(_tempRoot, "history.jsonl");
        _legacyPath = Path.Combine(_tempRoot, "history.json");
    }

    private JsonlHistoryStore CreateStore(int maxEntries = 50)
        => new(_historyPath, maxEntries, _legacyPath);

    [Fact]
    public async Task AppendAsync_PersistsEntriesAcrossStoreInstances()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Single(entries);
        Assert.Equal("git status", entries[0].CommandText);
    }

    [Fact]
    public async Task AppendAsync_WritesOneLinePerRecord()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));

        string[] lines = await File.ReadAllLinesAsync(_historyPath);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.NotNull(
            JsonSerializer.Deserialize(line, CommandAssistJsonLinesContext.Default.CommandHistoryEntry)));
    }

    /// <summary>
    /// Appends must not rewrite the file. Asserted structurally: the first record's bytes are still
    /// the first bytes on disk after later appends.
    /// </summary>
    [Fact]
    public async Task AppendAsync_LeavesEarlierLinesUntouched()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        string firstLine = (await File.ReadAllLinesAsync(_historyPath))[0];

        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));

        Assert.Equal(firstLine, (await File.ReadAllLinesAsync(_historyPath))[0]);
    }

    [Fact]
    public async Task AppendAsync_EnforcesRetentionLimitByKeepingMostRecentEntries()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 2);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore(maxEntries: 2).GetRecentAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.CommandText == "git status");
        Assert.Equal("npm run build", entries[0].CommandText);
        Assert.Equal("dotnet test", entries[1].CommandText);
    }

    /// <summary>
    /// An append-only log holds more physical lines than the cap until compaction reclaims them,
    /// but "history keeps N entries" has to mean the same thing to a reader either way. Reads
    /// enforce the cap so the promise does not drift with compaction timing.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_NeverReturnsMoreThanTheRetentionCap()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 2);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        // Three lines are still on disk - the file is far below the compaction floor.
        Assert.Equal(3, (await File.ReadAllLinesAsync(_historyPath)).Length);

        IReadOnlyList<CommandHistoryEntry> entries = await store.GetRecentAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("npm run build", entries[0].CommandText);
        Assert.Equal("dotnet test", entries[1].CommandText);
    }

    [Fact]
    public async Task SearchAsync_NeverReturnsCandidatesBeyondTheRetentionCap()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 1);

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("git stash pop", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));

        CommandHistoryEntry candidate = Assert.Single(await store.SearchAsync("git", maxCandidates: 10));

        Assert.Equal("git stash pop", candidate.CommandText);
    }

    [Fact]
    public async Task SetMaxEntries_WhenLowered_AppliesToTheLiveStoreAndCompactsTheFile()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 50);
        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        store.SetMaxEntries(1);

        CommandHistoryEntry kept = Assert.Single(await store.GetRecentAsync(10));
        Assert.Equal("npm run build", kept.CommandText);

        // The lowered cap reached the file, not just the in-memory view, so every later reader
        // agrees - including a fresh instance built after a restart.
        Assert.Single(await File.ReadAllLinesAsync(_historyPath));
        Assert.Single(await CreateStore(maxEntries: 1).GetRecentAsync(10));
    }

    [Fact]
    public async Task SetMaxEntries_WhenRaised_KeepsEntriesTheOldCapWouldHaveDropped()
    {
        JsonlHistoryStore store = CreateStore(maxEntries: 1);
        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));

        Assert.Single(await store.GetRecentAsync(10));

        store.SetMaxEntries(50);

        Assert.Equal(2, (await store.GetRecentAsync(10)).Count);
    }

    /// <summary>
    /// A whole-file read failure is not a corrupt line. The in-memory view is a prefix of the
    /// truth, so rewriting the file from it would delete everything past the failure point -
    /// which is exactly what treating the two the same used to do.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_WhenTheFileCannotBeRead_LeavesItIntactAndRecoversOnTheNextRead()
    {
        await File.WriteAllLinesAsync(_historyPath, new[]
        {
            Serialize(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"))),
            Serialize(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00"))),
            Serialize(CreateEntry("npm run build", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")))
        });

        JsonlHistoryStore store = CreateStore();

        IReadOnlyList<CommandHistoryEntry> duringFailure;
        using (new FileStream(_historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            duringFailure = await store.GetRecentAsync(10);
        }

        Assert.Empty(duringFailure);
        Assert.Equal(3, (await File.ReadAllLinesAsync(_historyPath)).Length);

        // The partial view was never cached as the truth, so the same instance heals itself.
        Assert.Equal(3, (await store.GetRecentAsync(10)).Count);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedHistory()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status"));
        await store.ClearAsync();

        Assert.Empty(await store.GetRecentAsync(10));
        Assert.Empty(await CreateStore().GetRecentAsync(10));
    }

    /// <summary>
    /// <strong>"Clear history" has to be true, and the backup the migration leaves behind is where a
    /// V1-captured secret would still be sitting.</strong>
    /// </summary>
    /// <remarks>
    /// The V1 Enter-time capture read a keystroke mirror with no echo check, so a password typed at a
    /// non-echoing prompt in a markless session was written to <c>history.json</c> verbatim -
    /// <c>SecretsFilter</c> matches nothing on a bare word. The migration copies those entries into
    /// <c>history.jsonl</c> unfiltered and renames the source to <c>.bak</c>, so truncating only the
    /// live log left exactly the entries the user was trying to erase on disk, under a confirmation
    /// prompt that said otherwise. A user who suspects a secret is in their history has one control
    /// and it has to work.
    /// </remarks>
    [Fact]
    public async Task ClearAsync_AlsoDeletesTheMigratedLegacyBackup()
    {
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(
                new List<CommandHistoryEntry> { CreateEntry("hunter2") },
                CommandAssistJsonContext.Default.ListCommandHistoryEntry));

        JsonlHistoryStore store = CreateStore();

        // The read is what runs the migration; after it, the secret lives in two files.
        Assert.Single(await store.GetRecentAsync(10));
        Assert.True(File.Exists(_legacyPath + ".bak"));

        await store.ClearAsync();

        Assert.Empty(await store.GetRecentAsync(10));
        Assert.False(File.Exists(_legacyPath + ".bak"));
    }

    /// <summary>
    /// And an un-migrated legacy file goes too, or the next launch migrates it straight back in.
    /// </summary>
    /// <remarks>
    /// Reachable because <c>ClearAsync</c> does not load: clearing before anything has read the store
    /// writes an empty <c>history.jsonl</c>, which then permanently suppresses the migration guard -
    /// so without this the entries would survive both the clear and every later read, invisible.
    /// </remarks>
    [Fact]
    public async Task ClearAsync_BeforeAnyRead_AlsoDeletesTheUnmigratedLegacyFile()
    {
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(
                new List<CommandHistoryEntry> { CreateEntry("hunter2") },
                CommandAssistJsonContext.Default.ListCommandHistoryEntry));

        await CreateStore().ClearAsync();

        Assert.False(File.Exists(_legacyPath));
        Assert.Empty(await CreateStore().GetRecentAsync(10));
    }

    [Fact]
    public async Task GetRecentAsync_WhenLineIsCorrupt_SkipsItAndKeepsTheRest()
    {
        await File.WriteAllLinesAsync(_historyPath, new[]
        {
            Serialize(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"))),
            "{ this is not valid json",
            Serialize(CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")))
        });

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("dotnet test", entries[0].CommandText);
        Assert.Equal("git status", entries[1].CommandText);
    }

    /// <summary>A crash mid-append truncates the last line; that costs one command, not the file.</summary>
    [Fact]
    public async Task GetRecentAsync_WhenFinalLineIsTruncated_KeepsPrecedingEntries()
    {
        string good = Serialize(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        string truncated = Serialize(CreateEntry("dotnet test"))[..20];
        await File.WriteAllTextAsync(_historyPath, good + Environment.NewLine + truncated);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Single(entries);
        Assert.Equal("git status", entries[0].CommandText);
    }

    [Fact]
    public async Task SearchAsync_ReturnsSubsequenceCandidatesMostRecentFirst()
    {
        JsonlHistoryStore store = CreateStore();

        await store.AppendAsync(CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("git stash pop", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("docker ps", executedAt: DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        IReadOnlyList<CommandHistoryEntry> entries = await store.SearchAsync("git sta", maxCandidates: 5);

        // Recall gate, not a ranking: both git rows qualify, "docker ps" does not, and the order is
        // recency because that is all a candidate cap can honestly promise.
        Assert.Equal(2, entries.Count);
        Assert.Equal("git stash pop", entries[0].CommandText);
        Assert.Equal("git status", entries[1].CommandText);
    }

    /// <summary>
    /// The gate admits exactly what <c>CommandAssistSuggestionEngine.ScoreText</c> would score above
    /// zero, which includes non-contiguous subsequence matches.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AdmitsSubsequenceMatchesTheEngineWouldScore()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("dotnet test"));

        Assert.Single(await store.SearchAsync("dtt", maxCandidates: 5));
        Assert.Empty(await store.SearchAsync("zzz", maxCandidates: 5));
    }

    [Fact]
    public async Task SearchAsync_WhenQueryIsEmpty_ReturnsEveryEntry()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("git status"));
        await store.AppendAsync(CreateEntry("dotnet test"));

        Assert.Equal(2, (await store.SearchAsync("  ", maxCandidates: 10)).Count);
    }

    [Fact]
    public async Task TryUpdateExecutionResultAsync_AppendsSupersedingRecordResolvedOnReload()
    {
        JsonlHistoryStore store = CreateStore();
        CommandHistoryEntry entry = CreateEntry(
            "git status",
            executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            exitCode: null);
        await store.AppendAsync(entry);

        bool updated = await store.TryUpdateExecutionResultAsync(entry.Id, 0, 2500);

        // Two physical lines, one live entry: the update is a superseding record, not a rewrite.
        Assert.Equal(2, (await File.ReadAllLinesAsync(_historyPath)).Length);
        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.True(updated);
        Assert.Single(entries);
        Assert.Equal(0, entries[0].ExitCode);
        Assert.Equal(2500, entries[0].DurationMs);
    }

    [Fact]
    public async Task TryUpdateExecutionResultAsync_WhenEntryIsUnknown_ReturnsFalseWithoutWriting()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("git status"));

        bool updated = await store.TryUpdateExecutionResultAsync("no-such-id", 1, 10);

        Assert.False(updated);
        Assert.Single(await File.ReadAllLinesAsync(_historyPath));
    }

    /// <summary>
    /// Later records win regardless of the entries' timestamps: resolution is by file position.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_WhenIdAppearsTwice_KeepsTheLastRecord()
    {
        CommandHistoryEntry original = CreateEntry("git status", exitCode: null);
        await File.WriteAllLinesAsync(_historyPath, new[]
        {
            Serialize(original),
            Serialize(original with { ExitCode = 42, CommandText = "git status --short" })
        });

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Single(entries);
        Assert.Equal(42, entries[0].ExitCode);
        Assert.Equal("git status --short", entries[0].CommandText);
    }

    [Fact]
    public async Task AppendAsync_WhenDeadRecordsDominate_CompactsTheFile()
    {
        JsonlHistoryStore store = CreateStore();
        CommandHistoryEntry entry = CreateEntry("git status", exitCode: null);
        await store.AppendAsync(entry);

        // Every update supersedes the same entry, so all but one line is dead. Past the minimum
        // line count the dead-ratio rule fires and the log collapses back to the live set.
        int updates = JsonlHistoryStore.CompactionMinimumLines + 4;
        for (int i = 0; i < updates; i++)
        {
            await store.TryUpdateExecutionResultAsync(entry.Id, i, i);
        }

        string[] lines = await File.ReadAllLinesAsync(_historyPath);

        Assert.True(
            lines.Length < JsonlHistoryStore.CompactionMinimumLines,
            $"Expected compaction to collapse the log, but {lines.Length} lines remain.");
        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);
        Assert.Single(entries);
        Assert.Equal(updates - 1, entries[0].ExitCode);
    }

    [Fact]
    public async Task GetRecentAsync_WhenFileExceedsCap_CompactsToTheCapOnLoad()
    {
        var lines = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            lines.Add(Serialize(CreateEntry(
                $"command-{i}",
                executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00").AddMinutes(i))));
        }

        await File.WriteAllLinesAsync(_historyPath, lines);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore(maxEntries: 5).GetRecentAsync(50);

        Assert.Equal(5, entries.Count);
        Assert.Equal("command-39", entries[0].CommandText);
        Assert.Equal(5, (await File.ReadAllLinesAsync(_historyPath)).Length);
    }

    [Fact]
    public async Task Load_WhenLegacyJsonExists_MigratesItAndBacksItUp()
    {
        var legacyEntries = new List<CommandHistoryEntry>
        {
            CreateEntry("git status", executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")),
            CreateEntry("dotnet test", executedAt: DateTimeOffset.Parse("2026-03-01T10:01:00+00:00"))
        };
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(legacyEntries, CommandAssistJsonContext.Default.ListCommandHistoryEntry));

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("dotnet test", entries[0].CommandText);
        Assert.True(File.Exists(_historyPath));
        Assert.Equal(2, (await File.ReadAllLinesAsync(_historyPath)).Length);

        // User data is renamed, never deleted.
        Assert.False(File.Exists(_legacyPath));
        Assert.True(File.Exists(_legacyPath + ".bak"));
    }

    [Fact]
    public async Task Load_WhenLegacyJsonExceedsCap_MigratesOnlyTheMostRecentEntries()
    {
        List<CommandHistoryEntry> legacyEntries = Enumerable.Range(0, 10)
            .Select(i => CreateEntry(
                $"command-{i}",
                executedAt: DateTimeOffset.Parse("2026-03-01T10:00:00+00:00").AddMinutes(i)))
            .ToList();
        await File.WriteAllTextAsync(
            _legacyPath,
            JsonSerializer.Serialize(legacyEntries, CommandAssistJsonContext.Default.ListCommandHistoryEntry));

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore(maxEntries: 3).GetRecentAsync(10);

        Assert.Equal(3, entries.Count);
        Assert.Equal("command-9", entries[0].CommandText);
    }

    [Fact]
    public async Task Load_WhenJsonlAlreadyExists_LeavesLegacyFileAlone()
    {
        await File.WriteAllTextAsync(_legacyPath, "[]");
        await File.WriteAllLinesAsync(_historyPath, new[] { Serialize(CreateEntry("git status")) });

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Single(entries);
        Assert.True(File.Exists(_legacyPath));
        Assert.False(File.Exists(_legacyPath + ".bak"));
    }

    [Fact]
    public async Task Load_WhenLegacyJsonIsCorrupt_StartsCleanAndStillBacksItUp()
    {
        await File.WriteAllTextAsync(_legacyPath, "{ this is not valid json");

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.Empty(entries);
        Assert.False(File.Exists(_legacyPath));
        Assert.True(File.Exists(_legacyPath + ".bak"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static string Serialize(CommandHistoryEntry entry)
        => JsonSerializer.Serialize(entry, CommandAssistJsonLinesContext.Default.CommandHistoryEntry);

    // ---------------------------- retroactive typo flagging (dogfood round 4, item 4)

    /// <summary>
    /// The owner's case. Three <c>gti</c> lines captured before the flag existed, all unflagged; he
    /// reproduces the typo once and every one of them is suppressed, without his having to retype the
    /// other two.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_FlagsEveryOlderEntryWithTheSameFirstToken()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.AppendAsync(CreateEntry("gti log --oneline", DateTimeOffset.Parse("2026-03-01T10:01:00+00:00")));
        await store.AppendAsync(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:02:00+00:00")));

        int flagged = await store.TryMarkInvalidCommandsByFirstTokenAsync("gti");

        Assert.Equal(2, flagged);

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);
        Assert.All(
            entries.Where(entry => entry.CommandText.StartsWith("gti", StringComparison.Ordinal)),
            entry => Assert.True(entry.IsInvalidCommand));

        // And nothing else moved: `git status` is a different program that happens to share a prefix.
        Assert.False(entries.Single(entry => entry.CommandText == "git status").IsInvalidCommand);
    }

    /// <summary>
    /// The sweep survives a reload, because it is written as superseding records rather than held in
    /// memory - a flag the next launch forgets is not a suppression.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_PersistsAcrossStoreInstances()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));

        await store.TryMarkInvalidCommandsByFirstTokenAsync("gti");

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);
        Assert.True(entries.Single().IsInvalidCommand);
    }

    /// <summary>
    /// Case-insensitive, matching how <c>CommandAssistSuggestionEngine</c> groups history: if the
    /// grouping treats two spellings as one row, the suppression has to reach both or the row survives.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_MatchesCaseInsensitively()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("GTI status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));

        Assert.Equal(1, await store.TryMarkInvalidCommandsByFirstTokenAsync("gti"));
    }

    /// <summary>
    /// Idempotent, and it says so in its return value: a user who mistypes the same word twice must not
    /// pay for a second pass of writes.
    /// </summary>
    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_SkipsEntriesAlreadyFlagged()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));
        await store.TryMarkInvalidCommandsByFirstTokenAsync("gti");

        int lineCount = (await File.ReadAllLinesAsync(_historyPath)).Length;

        Assert.Equal(0, await store.TryMarkInvalidCommandsByFirstTokenAsync("gti"));
        Assert.Equal(lineCount, (await File.ReadAllLinesAsync(_historyPath)).Length);
    }

    [Fact]
    public async Task TryMarkInvalidCommandsByFirstTokenAsync_WithABlankToken_DoesNothing()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00")));

        Assert.Equal(0, await store.TryMarkInvalidCommandsByFirstTokenAsync("   "));
        Assert.False((await store.GetRecentAsync(10)).Single().IsInvalidCommand);
    }

    /// <summary>
    /// The load-time backfill: an exit-127 line written before the flag existed comes back flagged,
    /// because the information was on disk the whole time and was simply not being read.
    /// </summary>
    [Fact]
    public async Task Load_BackfillsTheInvalidFlagForExitCode127()
    {
        // Written through the store so the line on disk is a real one - and with the flag left at its
        // default, which is exactly what a pre-flag record deserialises to.
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"), exitCode: 127));
        await store.AppendAsync(CreateEntry("git status", DateTimeOffset.Parse("2026-03-01T10:01:00+00:00"), exitCode: 0));
        await store.AppendAsync(CreateEntry("dotnet test", DateTimeOffset.Parse("2026-03-01T10:02:00+00:00"), exitCode: 1));

        IReadOnlyList<CommandHistoryEntry> entries = await CreateStore().GetRecentAsync(10);

        Assert.True(entries.Single(entry => entry.CommandText == "gti status").IsInvalidCommand);

        // Exactly the live capture rule and no wider: a command that ran and failed is not a typo.
        Assert.False(entries.Single(entry => entry.CommandText == "git status").IsInvalidCommand);
        Assert.False(entries.Single(entry => entry.CommandText == "dotnet test").IsInvalidCommand);
    }

    /// <summary>
    /// A never-completed entry has no exit code at all, and an absent code is not a 127.
    /// </summary>
    [Fact]
    public async Task Load_DoesNotBackfillAnEntryWithNoExitCode()
    {
        JsonlHistoryStore store = CreateStore();
        await store.AppendAsync(CreateEntry("gti status", DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"), exitCode: null));

        Assert.False((await CreateStore().GetRecentAsync(10)).Single().IsInvalidCommand);
    }

    [Theory]
    [InlineData("gti status", "gti")]
    [InlineData("   gti   status", "gti")]
    [InlineData("gti", "gti")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void FirstToken_IsTheLeadingWord(string commandText, string expected)
    {
        Assert.Equal(expected, CommandHistoryEntry.FirstToken(commandText));
    }

    private static CommandHistoryEntry CreateEntry(
        string commandText,
        DateTimeOffset? executedAt = null,
        int? exitCode = 0)
    {
        return new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: commandText,
            ExecutedAt: executedAt ?? DateTimeOffset.Parse("2026-03-01T10:00:00+00:00"),
            ShellKind: "pwsh",
            WorkingDirectory: @"C:\repo",
            ProfileId: "profile-1",
            SessionId: "session-1",
            HostId: null,
            ExitCode: exitCode,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null);
    }
}
