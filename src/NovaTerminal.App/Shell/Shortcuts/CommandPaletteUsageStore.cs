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
            _entries = deserialized is not null
                ? new Dictionary<string, CommandPaletteUsageEntry>(deserialized, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase);
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

                File.WriteAllText(path, json);
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
