using NovaTerminal.Shell;
using System.Text.Json;

namespace NovaTerminal.Tests.Core;

// Regression tests for #167: settings persistence must be crash-safe (atomic write
// + .bak) and corrupt files must fall back to the backup instead of silently
// resetting all configuration to defaults.
public sealed class TerminalSettingsPersistenceTests
{
    [Fact]
    public void LoadFromPath_CorruptFile_FallsBackToBackup()
    {
        string root = Directory.CreateTempSubdirectory("nova-settings-tests-").FullName;
        try
        {
            string path = Path.Combine(root, "settings.json");

            // A valid backup with a recognizable marker...
            var good = new TerminalSettings { FontFamily = "BackupMarkerFont" };
            File.WriteAllText(path + ".bak", JsonSerializer.Serialize(good, AppJsonContext.Default.TerminalSettings));

            // ...and a corrupt primary (truncated JSON, as a crash mid-write produces).
            File.WriteAllText(path, "{\"FontFamily\":\"Trunc");

            TerminalSettings loaded = TerminalSettings.LoadFromPath(path);

            Assert.Equal("BackupMarkerFont", loaded.FontFamily);
            // The corrupt file is quarantined for diagnosis, not destroyed.
            Assert.True(File.Exists(path + ".corrupt"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LoadFromPath_CorruptFile_NoBackup_FallsBackToDefaults()
    {
        string root = Directory.CreateTempSubdirectory("nova-settings-tests-").FullName;
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "not json at all");

            TerminalSettings loaded = TerminalSettings.LoadFromPath(path);

            Assert.NotNull(loaded);
            Assert.NotEmpty(loaded.Profiles);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AtomicFile_WriteAllText_KeepsBackupOfPreviousContent()
    {
        string root = Directory.CreateTempSubdirectory("nova-atomic-tests-").FullName;
        try
        {
            string path = Path.Combine(root, "file.json");

            AtomicFile.WriteAllText(path, "v1");
            AtomicFile.WriteAllText(path, "v2");

            Assert.Equal("v2", File.ReadAllText(path));
            Assert.Equal("v1", File.ReadAllText(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"), "temp file must not linger");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AtomicFile_WriteAllBytes_RoundTrips()
    {
        string root = Directory.CreateTempSubdirectory("nova-atomic-tests-").FullName;
        try
        {
            string path = Path.Combine(root, "vault.dat");
            byte[] payload = { 1, 2, 3, 250, 251, 252 };

            AtomicFile.WriteAllBytes(path, payload);

            Assert.Equal(payload, File.ReadAllBytes(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
