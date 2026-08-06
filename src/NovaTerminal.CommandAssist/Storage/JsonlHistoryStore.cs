using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Storage;

/// <summary>
/// Append-only JSON-Lines command history with an in-memory index and periodic compaction.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the whole-file-rewrite-per-append <c>JsonHistoryStore</c>: every command execution used
/// to deserialize the entire history, append one record, and serialize it all back. Here an append
/// is a single line written to the end of the file.
/// </para>
/// <para>
/// <b>Update strategy: append-a-superseding-record, last-write-wins on load.</b> Exit-code and
/// duration patches are written as a full record carrying the same <c>Id</c> and appended like any
/// other line; the loader keeps the last record seen for a given id. The alternative (rewrite the
/// line in place) would reintroduce the whole-file write this class exists to remove, and would
/// have to do it on the command-completion path where a partial write is most likely to be
/// interrupted. The cost is dead records, which compaction reclaims.
/// </para>
/// <para>
/// <b>Compaction</b> rewrites the file with one line per live entry, capped at
/// <c>maxEntries</c> most-recent. It runs when the dead-record ratio or the raw line count crosses
/// <see cref="CompactionDeadRatio"/> / <see cref="CompactionMinimumLines"/>, and always right after
/// a legacy migration or a load that dropped corrupted lines.
/// </para>
/// <para>
/// <b>Corruption tolerance:</b> an unparseable line is skipped, not fatal. A truncated final line
/// (a write interrupted by a crash) therefore costs one command, not the file. A failure to read
/// the <i>file</i> is a different thing entirely and is handled differently - see
/// <see cref="ReadFileUnsafeAsync"/>.
/// </para>
/// <para>
/// <b>One instance per file, for the process lifetime.</b> This store is stateful (the index and
/// the physical line count are cached), so two live instances over one file would each compact
/// from their own partial view and clobber the other's appends. The retention cap is therefore
/// mutable via <see cref="SetMaxEntries"/> rather than baked in at construction: a settings change
/// must not become an instance swap.
/// </para>
/// </remarks>
public sealed class JsonlHistoryStore : IHistoryStore
{
    /// <summary>Compact once at least this fraction of the file's lines are superseded or over cap.</summary>
    internal const double CompactionDeadRatio = 0.5;

    /// <summary>Below this line count the dead-ratio check is skipped: rewriting a small file buys nothing.</summary>
    internal const int CompactionMinimumLines = 64;

    private readonly string _filePath;
    private readonly string? _legacyFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Retention cap. Volatile because <see cref="SetMaxEntries"/> publishes from whatever thread
    /// applied the setting, without taking <see cref="_gate"/> (which an in-flight load may hold).
    /// </summary>
    private volatile int _maxEntries;

    /// <summary>Set by <see cref="SetMaxEntries"/>; consumed by the next gated operation.</summary>
    private volatile bool _capChangePendingCompaction;

    /// <summary>Live entries keyed by id, most recent write wins. Null until the first load.</summary>
    private Dictionary<string, CommandHistoryEntry>? _index;

    /// <summary>Physical lines currently in the file, including superseded ones.</summary>
    private int _lineCount;

    /// <summary>
    /// True when the last load could not read the file to the end. The in-memory view is then a
    /// prefix of the truth, and compaction - which rewrites the file from that view - would turn
    /// the partial read into permanent data loss.
    /// </summary>
    private bool _loadIncomplete;

    public JsonlHistoryStore(string filePath, int maxEntries, string? legacyJsonFilePath = null)
    {
        _filePath = filePath;
        _legacyFilePath = legacyJsonFilePath;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>
    /// Changes the retention cap on the live store. Takes effect on the next gated operation, which
    /// compacts if the new cap is already exceeded.
    /// </summary>
    /// <remarks>
    /// Exists so that a <c>CommandAssistMaxHistoryEntries</c> change never has to be expressed as a
    /// new store instance. See the type remarks for why two instances over one file is a
    /// data-losing arrangement.
    /// </remarks>
    public void SetMaxEntries(int maxEntries)
    {
        int clamped = Math.Max(1, maxEntries);
        if (_maxEntries == clamped)
        {
            return;
        }

        _maxEntries = clamped;
        _capChangePendingCompaction = true;
    }

    public async Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);

            // Disk first, then the index: a failed write must not leave a phantom entry that the
            // next compaction would materialize into the file as though it had been committed.
            await AppendLineUnsafeAsync(entry, cancellationToken);
            index[entry.Id] = entry;
            await CompactIfNeededUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the entries whose text could plausibly match <paramref name="query"/>, most recent
    /// first, capped at <paramref name="maxCandidates"/>.
    /// </summary>
    /// <remarks>
    /// This is a recall gate, not a ranking. The store used to score candidates with its own
    /// lower-fidelity copy of <c>CommandAssistSuggestionEngine.ScoreText</c> and hand back the top
    /// N by that score, which meant two ranking implementations disagreed about the same history.
    /// Now the gate admits exactly what the engine would score above zero - a case-insensitive
    /// subsequence match, which subsumes prefix, token-prefix and containment - and ordering is
    /// left to the engine. Recency is the only ordering applied here, and only because truncating
    /// to <paramref name="maxCandidates"/> requires picking which candidates to drop.
    /// <para>
    /// The retention cap is applied before candidacy, not after: an append-only log holds more
    /// lines than the cap until compaction reclaims them, and "history keeps N entries" has to
    /// mean the same thing to a reader whether or not compaction has run yet.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            string normalized = query.Trim();

            return index.Values
                .OrderByDescending(entry => entry.ExecutedAt)
                .Take(_maxEntries)
                .Where(entry => IsCandidate(entry.CommandText, normalized))
                .Take(Math.Max(0, maxCandidates))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <remarks>Capped by the retention limit as well as <paramref name="maxResults"/>; see
    /// <see cref="SearchAsync"/> for why.</remarks>
    public async Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            return index.Values
                .OrderByDescending(entry => entry.ExecutedAt)
                .Take(Math.Min(Math.Max(0, maxResults), _maxEntries))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes every recorded command: the live log, the un-migrated V1 file, and the backup the
    /// migration leaves behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two legacy files are the privacy half of this method, added after the owner's
    /// "I suspect it captures my passwords" report.</strong> The V1 capture path that Phase 1c
    /// deleted read a keystroke mirror with no echo check at all, so a password typed at a
    /// non-echoing prompt in a markless session <em>was</em> written to <c>history.json</c> verbatim
    /// - <c>SecretsFilter</c> matches nothing on a bare word. Those entries are then copied into
    /// <c>history.jsonl</c> unfiltered by
    /// <see cref="TryMigrateLegacyFileUnsafeAsync"/>, and the source is renamed to
    /// <c>history.json.bak</c> rather than deleted.
    /// </para>
    /// <para>
    /// Truncating only <c>history.jsonl</c> therefore left the very entries the user is trying to
    /// erase sitting on disk in the backup, while the confirmation prompt said "this deletes every
    /// recorded command". A user who suspects a secret is in their history has exactly one control,
    /// and it has to be true.
    /// </para>
    /// <para>
    /// Deleting the un-migrated <c>history.json</c> matters for a second reason: without it, a
    /// <c>ClearAsync</c> that runs before the first read (which is a legal order - it does not load)
    /// would leave the legacy file to be migrated in on the next launch, re-materialising everything
    /// the user just cleared.
    /// </para>
    /// <para>
    /// Deletion failures are swallowed. A locked backup file must not turn "clear my history" into
    /// an error that leaves the live log intact, which is the strictly worse outcome.
    /// </para>
    /// </remarks>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _index = new Dictionary<string, CommandHistoryEntry>(StringComparer.Ordinal);
            _loadIncomplete = false;
            _capChangePendingCompaction = false;
            await WriteAllUnsafeAsync(Array.Empty<CommandHistoryEntry>(), cancellationToken);
            DeleteLegacyFilesUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the pre-JSONL history file and the backup the migration renames it to. Best-effort.
    /// </summary>
    private void DeleteLegacyFilesUnsafe()
    {
        if (string.IsNullOrWhiteSpace(_legacyFilePath))
        {
            return;
        }

        foreach (string path in new[] { _legacyFilePath!, _legacyFilePath + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // See the remarks on ClearAsync: a locked legacy file must not fail the clear.
            }
        }
    }

    public async Task<bool> TryUpdateExecutionResultAsync(
        string entryId,
        int? exitCode,
        long? durationMs,
        CancellationToken cancellationToken = default,
        bool isInvalidCommand = false)
    {
        return await TryPatchAsync(
            entryId,
            existing => existing with
            {
                ExitCode = exitCode,
                DurationMs = durationMs ?? existing.DurationMs,

                // Latching, never clearing. See the parameter docs on IHistoryStore: the exit-code
                // signal and the recogniser signal arrive independently and in either order, so a
                // patch that has nothing to say about the classification must leave it alone.
                IsInvalidCommand = existing.IsInvalidCommand || isInvalidCommand
            },
            cancellationToken);
    }

    public async Task<bool> TryMarkInvalidCommandAsync(string entryId, CancellationToken cancellationToken = default)
    {
        return await TryPatchAsync(
            entryId,
            existing => existing with { IsInvalidCommand = true },
            cancellationToken);
    }

    private async Task<bool> TryPatchAsync(
        string entryId,
        Func<CommandHistoryEntry, CommandHistoryEntry> patch,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            if (!index.TryGetValue(entryId, out CommandHistoryEntry? existing))
            {
                return false;
            }

            CommandHistoryEntry updated = patch(existing);

            // Disk first, then the index, for the same reason as AppendAsync.
            await AppendLineUnsafeAsync(updated, cancellationToken);
            index[entryId] = updated;
            await CompactIfNeededUnsafeAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Admits every entry the ranking engine would score above zero for this query: a
    /// case-insensitive subsequence match. Prefix, token-prefix and containment are all
    /// subsequences, so this single test is the union of the engine's four text signals.
    /// </summary>
    private static bool IsCandidate(string commandText, string query)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return IsSubsequence(query.ToLowerInvariant(), commandText.ToLowerInvariant());
    }

    private static bool IsSubsequence(string query, string text)
    {
        int queryIndex = 0;
        for (int i = 0; i < text.Length && queryIndex < query.Length; i++)
        {
            if (text[i] == query[queryIndex])
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length;
    }

    private async Task<Dictionary<string, CommandHistoryEntry>> EnsureLoadedUnsafeAsync(CancellationToken cancellationToken)
    {
        // A partial view is never cached as the truth: re-read until the file comes back whole, so
        // a transient lock (antivirus, a backup agent) heals itself on the next operation.
        if (_index != null && !_loadIncomplete)
        {
            if (_capChangePendingCompaction)
            {
                _capChangePendingCompaction = false;
                await CompactUnsafeAsync(cancellationToken);
            }

            return _index!;
        }

        bool migrated = await TryMigrateLegacyFileUnsafeAsync(cancellationToken);
        (Dictionary<string, CommandHistoryEntry> index, int lineCount, bool sawCorruptLine, bool readFailed) =
            await ReadFileUnsafeAsync(cancellationToken);

        _index = index;
        _lineCount = lineCount;
        _loadIncomplete = readFailed;

        // A fresh read already honors whatever the cap is now.
        _capChangePendingCompaction = false;

        // A migration writes a clean file already; a corrupted or over-cap file is worth rewriting
        // once at startup so the damage does not accumulate across sessions. A file that could not
        // be read to the end is worth rewriting never - see ReadFileUnsafeAsync.
        if (!migrated && !readFailed &&
            (sawCorruptLine || index.Count > _maxEntries || IsCompactionDue(lineCount, index.Count)))
        {
            await CompactUnsafeAsync(cancellationToken);
        }

        return _index!;
    }

    /// <summary>
    /// One-time conversion of the pre-JSONL <c>history.json</c>. The legacy file is renamed to
    /// <c>.bak</c> rather than deleted: it is user data, and a bad conversion should be recoverable.
    /// </summary>
    private async Task<bool> TryMigrateLegacyFileUnsafeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_legacyFilePath) ||
            File.Exists(_filePath) ||
            !File.Exists(_legacyFilePath))
        {
            return false;
        }

        List<CommandHistoryEntry> legacyEntries;
        try
        {
            await using FileStream stream = File.OpenRead(_legacyFilePath);
            legacyEntries = await JsonSerializer.DeserializeAsync(
                stream,
                CommandAssistJsonContext.Default.ListCommandHistoryEntry,
                cancellationToken) ?? new List<CommandHistoryEntry>();
        }
        catch
        {
            // An unreadable legacy file is not worth failing startup over, but it must not be
            // retried forever either: fall through and back it up so the next launch starts clean.
            legacyEntries = new List<CommandHistoryEntry>();
        }

        try
        {
            await WriteAllUnsafeAsync(
                legacyEntries
                    .OrderByDescending(entry => entry.ExecutedAt)
                    .Take(_maxEntries)
                    .Reverse(),
                cancellationToken);

            string backupPath = _legacyFilePath + ".bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(_legacyFilePath, backupPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the log into an index, separating the two failure kinds that must not be conflated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SawCorruptLine</b> - one line did not parse. The rest of the file was read, so the
    /// in-memory view is complete and compacting it is a repair: the bad line is dropped once
    /// instead of being re-skipped every launch.
    /// </para>
    /// <para>
    /// <b>ReadFailed</b> - the file itself could not be read to the end. The in-memory view is a
    /// prefix of the truth. Compacting <i>that</i> would rewrite the file to the prefix and destroy
    /// everything past the failure point, so the caller must not, and must not cache the view as
    /// authoritative either. The partial index is still returned, because a partially readable
    /// history beats no history for the reads that happen before the next retry.
    /// </para>
    /// </remarks>
    private async Task<(Dictionary<string, CommandHistoryEntry> Index, int LineCount, bool SawCorruptLine, bool ReadFailed)> ReadFileUnsafeAsync(
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, CommandHistoryEntry>(StringComparer.Ordinal);
        int lineCount = 0;
        bool sawCorruptLine = false;

        if (!File.Exists(_filePath))
        {
            return (index, lineCount, sawCorruptLine, false);
        }

        try
        {
            using var reader = new StreamReader(_filePath, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lineCount++;
                CommandHistoryEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize(line, CommandAssistJsonContext.Default.CommandHistoryEntry);
                }
                catch (JsonException)
                {
                    sawCorruptLine = true;
                    continue;
                }

                if (entry == null || string.IsNullOrEmpty(entry.Id))
                {
                    sawCorruptLine = true;
                    continue;
                }

                // Last write wins: a superseding record patches the entry it shares an id with.
                index[entry.Id] = entry;
            }
        }
        catch (IOException)
        {
            // Keep whatever was read; a partially readable file still beats an empty history. But
            // report it as a failed read, never as a corrupt line: the difference is whether the
            // caller is allowed to rewrite the file from this view.
            return (index, lineCount, sawCorruptLine, true);
        }

        return (index, lineCount, sawCorruptLine, false);
    }

    private async Task AppendLineUnsafeAsync(CommandHistoryEntry entry, CancellationToken cancellationToken)
    {
        EnsureDirectory();

        await using var stream = new FileStream(
            _filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await writer.WriteLineAsync(Serialize(entry).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        _lineCount++;
    }

    private bool IsCompactionDue(int lineCount, int liveCount)
    {
        // Rewriting the file from a prefix of it is how a transient read error becomes permanent
        // data loss. Let the log grow instead; the next clean load compacts it.
        if (_loadIncomplete)
        {
            return false;
        }

        if (lineCount < CompactionMinimumLines)
        {
            return false;
        }

        int dead = lineCount - Math.Min(liveCount, _maxEntries);
        return dead >= lineCount * CompactionDeadRatio;
    }

    private Task CompactIfNeededUnsafeAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, CommandHistoryEntry> index = _index!;
        return IsCompactionDue(_lineCount, index.Count)
            ? CompactUnsafeAsync(cancellationToken)
            : Task.CompletedTask;
    }

    private Task CompactUnsafeAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, CommandHistoryEntry> index = _index!;
        List<CommandHistoryEntry> kept = index.Values
            .OrderByDescending(entry => entry.ExecutedAt)
            .Take(_maxEntries)
            .ToList();

        _index = kept.ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        // Oldest first on disk so the file reads as an append log.
        kept.Reverse();
        return WriteAllUnsafeAsync(kept, cancellationToken);
    }

    private async Task WriteAllUnsafeAsync(IEnumerable<CommandHistoryEntry> entries, CancellationToken cancellationToken)
    {
        EnsureDirectory();

        string tempPath = _filePath + ".tmp";
        int written = 0;

        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (CommandHistoryEntry entry in entries)
            {
                await writer.WriteLineAsync(Serialize(entry).AsMemory(), cancellationToken);
                written++;
            }

            await writer.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, _filePath, overwrite: true);
        _lineCount = written;
    }

    private static string Serialize(CommandHistoryEntry entry)
    {
        // CommandAssistJsonLinesContext, not CommandAssistJsonContext: the latter is configured
        // WriteIndented so the legacy whole-file JSON round-trips byte-identically, and an indented
        // record spans many lines - which is precisely what this format cannot have.
        return JsonSerializer.Serialize(entry, CommandAssistJsonLinesContext.Default.CommandHistoryEntry);
    }

    private void EnsureDirectory()
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
