using System.Text.Json;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class TerminalSettingsFontTests
{
    [Fact]
    public void TerminalSettings_DefaultFontFamily_IsBundledCascadiaMonoPl()
    {
        var settings = new TerminalSettings();

        Assert.Equal(BundledFontCatalog.DefaultTerminalFontFamily, settings.FontFamily);
    }

    [Fact]
    public void LoadFromPath_PreservesExplicitSavedFontFamily()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string settingsPath = Path.Combine(tempRoot, "settings.json");
            var saved = new TerminalSettings
            {
                FontFamily = "JetBrains Mono"
            };

            string json = JsonSerializer.Serialize(saved, AppJsonContext.Default.TerminalSettings);
            File.WriteAllText(settingsPath, json);

            TerminalSettings loaded = TerminalSettings.LoadFromPath(settingsPath);

            Assert.Equal("JetBrains Mono", loaded.FontFamily);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_terminal_settings_font_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
