using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NovaTerminal.Core.Shortcuts;

public sealed class CommandPaletteUsageStore
{
    private readonly string _path;
    private Dictionary<string, CommandPaletteUsageEntry>? _entries;

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
            _entries = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringCommandPaletteUsageEntry)
                ?? new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase);
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
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(entries, AppJsonContext.Default.DictionaryStringCommandPaletteUsageEntry);
        File.WriteAllText(_path, json);
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
