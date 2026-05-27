using NovaTerminal.Core.Shortcuts;

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
                () => File.Exists(path) && File.ReadAllText(path).Contains("settings", StringComparison.Ordinal),
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
