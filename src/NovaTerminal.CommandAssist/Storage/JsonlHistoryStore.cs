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
/// (a write interrupted by a crash) therefore costs one command, not the file.
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
    private readonly int _maxEntries;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Live entries keyed by id, most recent write wins. Null until the first load.</summary>
    private Dictionary<string, CommandHistoryEntry>? _index;

    /// <summary>Physical lines currently in the file, including superseded ones.</summary>
    private int _lineCount;

    public JsonlHistoryStore(string filePath, int maxEntries, string? legacyJsonFilePath = null)
    {
        _filePath = filePath;
        _legacyFilePath = legacyJsonFilePath;
        _maxEntries = Math.Max(1, maxEntries);
    }

    public async Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            index[entry.Id] = entry;
            await AppendLineUnsafeAsync(entry, cancellationToken);
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
    /// </remarks>
    public async Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            string normalized = query.Trim();

            return index.Values
                .Where(entry => IsCandidate(entry.CommandText, normalized))
                .OrderByDescending(entry => entry.ExecutedAt)
                .Take(Math.Max(0, maxCandidates))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            return index.Values
                .OrderByDescending(entry => entry.ExecutedAt)
                .Take(Math.Max(0, maxResults))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _index = new Dictionary<string, CommandHistoryEntry>(StringComparer.Ordinal);
            await WriteAllUnsafeAsync(Array.Empty<CommandHistoryEntry>(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CommandHistoryEntry> index = await EnsureLoadedUnsafeAsync(cancellationToken);
            if (!index.TryGetValue(entryId, out CommandHistoryEntry? existing))
            {
                return false;
            }

            CommandHistoryEntry updated = existing with
            {
                ExitCode = exitCode,
                DurationMs = durationMs ?? existing.DurationMs
            };

            index[entryId] = updated;
            await AppendLineUnsafeAsync(updated, cancellationToken);
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
        if (_index != null)
        {
            return _index;
        }

        bool migrated = await TryMigrateLegacyFileUnsafeAsync(cancellationToken);
        (Dictionary<string, CommandHistoryEntry> index, int lineCount, bool sawCorruptLine) = await ReadFileUnsafeAsync(cancellationToken);

        _index = index;
        _lineCount = lineCount;

        // A migration writes a clean file already; a corrupted or over-cap file is worth rewriting
        // once at startup so the damage does not accumulate across sessions.
        if (!migrated && (sawCorruptLine || index.Count > _maxEntries || IsCompactionDue(lineCount, index.Count)))
        {
            await CompactUnsafeAsync(cancellationToken);
        }

        return _index;
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

    private async Task<(Dictionary<string, CommandHistoryEntry> Index, int LineCount, bool SawCorruptLine)> ReadFileUnsafeAsync(
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, CommandHistoryEntry>(StringComparer.Ordinal);
        int lineCount = 0;
        bool sawCorruptLine = false;

        if (!File.Exists(_filePath))
        {
            return (index, lineCount, sawCorruptLine);
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
            // Keep whatever was read; a partially readable file still beats an empty history.
            sawCorruptLine = true;
        }

        return (index, lineCount, sawCorruptLine);
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
