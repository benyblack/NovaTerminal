using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.Core;

namespace NovaTerminal.CommandAssist.Storage;

public sealed class JsonHistoryStore : IHistoryStore
{
    private readonly string _filePath;
    private readonly int _maxEntries;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonHistoryStore(string filePath, int maxEntries)
    {
        _filePath = filePath;
        _maxEntries = Math.Max(1, maxEntries);
    }

    public async Task AppendAsync(CommandHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CommandHistoryEntry> entries = await LoadEntriesUnsafeAsync(cancellationToken);
            entries.Add(entry);
            entries = entries
                .OrderByDescending(x => x.ExecutedAt)
                .Take(_maxEntries)
                .ToList();

            await SaveEntriesUnsafeAsync(entries, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CommandHistoryEntry>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CommandHistoryEntry> entries = await LoadEntriesUnsafeAsync(cancellationToken);
            string normalized = query.Trim();

            return entries
                .Select(entry => new
                {
                    Entry = entry,
                    Score = Score(entry.CommandText, normalized)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Entry.CommandText.Length)
                .ThenByDescending(x => x.Entry.ExecutedAt)
                .Take(maxResults)
                .Select(x => x.Entry)
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
            List<CommandHistoryEntry> entries = await LoadEntriesUnsafeAsync(cancellationToken);
            return entries
                .OrderByDescending(x => x.ExecutedAt)
                .Take(maxResults)
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
            await SaveEntriesUnsafeAsync(new List<CommandHistoryEntry>(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> TryUpdateExitCodeAsync(string entryId, int? exitCode, CancellationToken cancellationToken = default)
    {
        return TryUpdateExecutionResultAsync(entryId, exitCode, durationMs: null, cancellationToken);
    }

    public async Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CommandHistoryEntry> entries = await LoadEntriesUnsafeAsync(cancellationToken);
            int index = entries.FindIndex(x => x.Id == entryId);
            if (index < 0)
            {
                return false;
            }

            entries[index] = entries[index] with
            {
                ExitCode = exitCode,
                DurationMs = durationMs ?? entries[index].DurationMs
            };
            await SaveEntriesUnsafeAsync(entries, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<CommandHistoryEntry>> LoadEntriesUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<CommandHistoryEntry>();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            List<CommandHistoryEntry>? entries = await JsonSerializer.DeserializeAsync(
                stream,
                AppJsonContext.Default.ListCommandHistoryEntry,
                cancellationToken);

            return entries ?? new List<CommandHistoryEntry>();
        }
        catch
        {
            return new List<CommandHistoryEntry>();
        }
    }

    private async Task SaveEntriesUnsafeAsync(List<CommandHistoryEntry> entries, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, entries, AppJsonContext.Default.ListCommandHistoryEntry, cancellationToken);
    }

    private static int Score(string commandText, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 1;
        }

        string lowerCommand = commandText.ToLowerInvariant();
        string lowerQuery = query.ToLowerInvariant();
        if (lowerCommand.StartsWith(lowerQuery, StringComparison.Ordinal))
        {
            return 100;
        }

        string[] tokens = lowerCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(token => token.StartsWith(lowerQuery, StringComparison.Ordinal)))
        {
            return 80;
        }

        if (lowerCommand.Contains(lowerQuery, StringComparison.Ordinal))
        {
            return 40;
        }

        return IsSubsequence(lowerQuery, lowerCommand) ? 20 : 0;
    }

    private static bool IsSubsequence(string query, string command)
    {
        int queryIndex = 0;
        for (int i = 0; i < command.Length && queryIndex < query.Length; i++)
        {
            if (command[i] == query[queryIndex])
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length;
    }
}
