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
}
