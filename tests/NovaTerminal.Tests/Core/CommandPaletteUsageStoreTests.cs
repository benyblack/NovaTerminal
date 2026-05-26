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

            var reloaded = new CommandPaletteUsageStore(path);
            IReadOnlyDictionary<string, CommandPaletteUsageEntry> snapshot = reloaded.Load();

            Assert.Equal(1, snapshot["settings"].UseCount);
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
}
