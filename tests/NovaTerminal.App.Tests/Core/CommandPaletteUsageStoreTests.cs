using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class CommandPaletteUsageStoreTests
{
    [Fact]
    public void RecordUse_PersistsCountAcrossReloads()
    {
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            var store = new CommandPaletteUsageStore(path);
            store.RecordUse("settings", new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero));
            store.Save();
            WaitFor(
                () => ReadAllTextOrNull(path)?.Contains("settings", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(2));

            var reloaded = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = reloaded.Load();

            Assert.Equal(1, snapshot["settings"].UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_UsesCaseInsensitiveDictionaryForDeserializedEntries()
    {
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "Settings": {
                    "CommandId": "Settings",
                    "UseCount": 3,
                    "LastUsedAt": "2026-05-26T12:00:00+00:00"
                  }
                }
                """);

            var store = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = store.Load();

            Assert.True(snapshot.TryGetValue("settings", out CommandPaletteUsageEntry? entry));
            Assert.Equal(3, entry.UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_KeysDifferingOnlyByCase_DoNotThrow_AndLastOneWins()
    {
        string tempRoot = CreateTempDirectory();
        string path = Path.Combine(tempRoot, "command-palette-usage.json");

        try
        {
            // JSON keys are ordinal-distinct, so "settings" and "Settings" both survive
            // deserialization as sibling entries of a case-sensitive dictionary. Normalizing that
            // to OrdinalIgnoreCase must not throw on the collision -- and it must not degrade to
            // the empty-dictionary catch-all either, which is what the copy constructor's
            // ArgumentException produced. Per the documented last-wins rule, the key enumerated
            // last (here "Settings", in JSON document order) determines the surviving entry.
            File.WriteAllText(
                path,
                """
                {
                  "settings": {
                    "CommandId": "settings",
                    "UseCount": 3,
                    "LastUsedAt": "2026-05-26T12:00:00+00:00"
                  },
                  "Settings": {
                    "CommandId": "Settings",
                    "UseCount": 7,
                    "LastUsedAt": "2026-05-27T12:00:00+00:00"
                  }
                }
                """);

            var store = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = store.Load();

            Assert.Single(snapshot);
            Assert.True(snapshot.TryGetValue("SETTINGS", out CommandPaletteUsageEntry? entry));
            Assert.Equal(7, entry.UseCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// Reads <paramref name="path"/>, treating "not there yet" and "momentarily unreadable" alike.
    /// <see cref="CommandPaletteUsageStore.Save"/> writes on a background task, and File.WriteAllText
    /// holds a write handle the whole time; a File.ReadAllText landing inside that window fails with
    /// a sharing violation on Windows. Polling for the write to land must treat that IOException as
    /// "keep waiting" rather than letting it escape the predicate and fail the test.
    /// </summary>
    private static string? ReadAllTextOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_usage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }

        Assert.True(condition());
    }
}
