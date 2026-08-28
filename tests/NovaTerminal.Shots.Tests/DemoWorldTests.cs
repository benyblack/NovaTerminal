using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class DemoWorldTests
{
    private static string NewBaseDir() =>
        Path.Combine(Path.GetTempPath(), "nova-shots-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SeedSettings_WritesSettingsUnderTheIsolatedRoot_NotTheRealProfile()
    {
        string baseDir = NewBaseDir();
        using var world = DemoWorld.Create(baseDir);

        world.SeedSettings();

        string settingsPath = Path.Combine(world.ProfileRoot, "settings.json");
        Assert.True(File.Exists(settingsPath), $"Expected seeded settings at {settingsPath}.");
        Assert.StartsWith(baseDir, world.ProfileRoot, StringComparison.Ordinal);
        Assert.Equal(world.ProfileRoot, Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT"));
    }

    [Fact]
    public void SeedSettings_PinsTheDemoProfileAsDefault()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedSettings();

        string json = File.ReadAllText(Path.Combine(world.ProfileRoot, "settings.json"));
        Assert.Contains("\"Demo\"", json, StringComparison.Ordinal);
        Assert.Equal("Demo", world.DemoProfile.Name);
    }

    [Fact]
    public void Dispose_RemovesEverythingItCreated()
    {
        string baseDir = NewBaseDir();
        var world = DemoWorld.Create(baseDir);
        world.SeedSettings();

        world.Dispose();

        Assert.False(Directory.Exists(baseDir), "DemoWorld left files behind after disposal.");
    }

    // AppPaths.EnsureInitialized() only creates its directory scaffolding (themes/, logs/,
    // sessions/, workspaces/, policy/, recordings/, command-assist/, ssh/, ...) the FIRST time
    // it runs in a process - it is gated by a private static bool, not keyed by root. So the
    // second (and every later) DemoWorld created in this test process must not silently rely on
    // that one-shot behavior; it must scaffold its own ProfileRoot itself.
    [Fact]
    public void SeedSettings_ScaffoldsAppPathsDirectories_ForASecondDemoWorldInTheSameProcess()
    {
        using var first = DemoWorld.Create(NewBaseDir());
        first.SeedSettings();

        using var second = DemoWorld.Create(NewBaseDir());
        second.SeedSettings();

        string[] expectedSubdirectories =
        {
            "themes", "logs", "sessions", "workspaces", "workspace_templates",
            "policy", "recordings", "command-assist", "ssh"
        };

        foreach (string subdirectory in expectedSubdirectories)
        {
            string path = Path.Combine(second.ProfileRoot, subdirectory);
            Assert.True(Directory.Exists(path), $"Expected {path} to exist under the second DemoWorld's ProfileRoot.");
        }
    }
}
