using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Shell.Shortcuts;

public sealed class CommandPaletteUsageStore
{
    private readonly string _path;
    private Dictionary<string, CommandPaletteUsageEntry>? _entries;
    private int _saveVersion;

    public CommandPaletteUsageStore(string path)
    {
        _path = path;
    }

    public IReadOnlyDictionary<string, CommandPaletteUsageEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        if (!File.Exists(_path))
        {
            _entries = new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase);
            return _entries;
        }

        try
        {
            string json = File.ReadAllText(_path);
            Dictionary<string, CommandPaletteUsageEntry>? deserialized = JsonSerializer.Deserialize(
                json,
                AppJsonContext.Default.DictionaryStringCommandPaletteUsageEntry);
            // Rebuilt with an explicit assignment loop rather than the
            // `new Dictionary(source, comparer)` copy constructor: that constructor adds entries
            // one by one under the *new* comparer and throws ArgumentException the moment two
            // source keys collapse to the same key. `deserialized` carries the default ordinal
            // comparer, so sibling JSON entries "find" and "Find" both survive deserialization
            // intact and would collapse here. This file is hand-editable, and the catch below
            // would swallow the throw by discarding the *entire* usage history -- so a
            // typo-shaped duplicate must degrade to last-one-wins (in source enumeration order)
            // by plain indexer assignment, which cannot throw.
            _entries = new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase);
            if (deserialized is not null)
            {
                foreach (KeyValuePair<string, CommandPaletteUsageEntry> kv in deserialized)
                {
                    _entries[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            _entries = new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase);
        }

        return _entries;
    }

    public void RecordUse(string commandId, DateTimeOffset usedAt)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        Dictionary<string, CommandPaletteUsageEntry> entries = EnsureEntries();
        if (entries.TryGetValue(commandId, out CommandPaletteUsageEntry? entry))
        {
            entries[commandId] = entry with
            {
                UseCount = entry.UseCount + 1,
                LastUsedAt = usedAt,
            };
            return;
        }

        entries[commandId] = new CommandPaletteUsageEntry(commandId, 1, usedAt);
    }

    public void Save()
    {
        Dictionary<string, CommandPaletteUsageEntry> entries = EnsureEntries();
        Dictionary<string, CommandPaletteUsageEntry> snapshot = new(entries, StringComparer.OrdinalIgnoreCase);
        string json = JsonSerializer.Serialize(snapshot, AppJsonContext.Default.DictionaryStringCommandPaletteUsageEntry);
        string path = _path;
        string? directory = Path.GetDirectoryName(_path);
        int version = Interlocked.Increment(ref _saveVersion);

        _ = Task.Run(() =>
        {
            if (version != Volatile.Read(ref _saveVersion))
            {
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write via a temp sibling + atomic rename (AtomicFile, same pattern as
                // SessionManager/TerminalSettings) so a concurrent reader never observes a
                // truncated file. The old File.WriteAllText held a write handle for up to 850ms
                // under contention (its own sharing-violation window), which this change
                // eliminates - the write itself is no longer where a concurrent reader can catch
                // it. What is left, and what this retry actually guards against, is the rename
                // step alone: on Windows a rename over an open handle needs FILE_SHARE_DELETE, so
                // if a reader happens to have the destination open at that instant the rename
                // throws - a reader's hold is brief (open, read, close), so a couple of short
                // retries clears it rather than silently dropping the save.
                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        AtomicFile.WriteAllText(path, json);
                        break;
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        Thread.Sleep(20);
                    }
                }
            }
            catch
            {
                // Keep usage tracking best-effort only.
            }
        });
    }

    private Dictionary<string, CommandPaletteUsageEntry> EnsureEntries()
    {
        if (_entries is null)
        {
            _entries = new Dictionary<string, CommandPaletteUsageEntry>(Load(), StringComparer.OrdinalIgnoreCase);
        }

        return _entries;
    }
}
