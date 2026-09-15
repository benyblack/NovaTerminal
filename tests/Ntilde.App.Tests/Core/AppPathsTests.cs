using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class AppPathsTests
{
    [Fact]
    public void RootDirectory_UsesEnvironmentOverrideWhenSet()
    {
        string tempRoot = CreateTempDirectory();
        string? previous = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");

        try
        {
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", tempRoot);

            Assert.Equal(Path.GetFullPath(tempRoot), Path.GetFullPath(AppPaths.RootDirectory));
            Assert.Equal(
                Path.Combine(Path.GetFullPath(tempRoot), "sessions", "last_session.json"),
                Path.GetFullPath(AppPaths.SessionFilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", previous);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void RootDirectory_IsUnderLocalApplicationData()
    {
        string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string root = Path.GetFullPath(AppPaths.RootDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(localAppData, root, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.StartsWith(localAppData, root, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MigrateFileIfNeeded_Copies_WhenDestinationMissing()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string source = Path.Combine(tempRoot, "source.txt");
            string destination = Path.Combine(tempRoot, "dest", "target.txt");
            File.WriteAllText(source, "source-content");

            AppPaths.MigrateFileIfNeeded(source, destination);

            Assert.True(File.Exists(destination));
            Assert.Equal("source-content", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MigrateFileIfNeeded_DoesNotOverwrite_NewerDestination()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string source = Path.Combine(tempRoot, "source.txt");
            string destination = Path.Combine(tempRoot, "destination.txt");
            File.WriteAllText(source, "old-source");
            File.WriteAllText(destination, "new-destination");

            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);

            AppPaths.MigrateFileIfNeeded(source, destination);

            Assert.Equal("new-destination", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MigrateDirectoryIfNeeded_CopiesNestedFiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string nestedSourceDir = Path.Combine(sourceDir, "nested");
            Directory.CreateDirectory(nestedSourceDir);

            string sourceFile = Path.Combine(nestedSourceDir, "theme.json");
            File.WriteAllText(sourceFile, "{ \"name\": \"test\" }");

            string destinationDir = Path.Combine(tempRoot, "destination");
            AppPaths.MigrateDirectoryIfNeeded(sourceDir, destinationDir);

            string migratedFile = Path.Combine(destinationDir, "nested", "theme.json");
            Assert.True(File.Exists(migratedFile));
            Assert.Equal("{ \"name\": \"test\" }", File.ReadAllText(migratedFile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void NativeKnownHostsFilePath_IsStableUnderRootDirectory()
    {
        string fullPath = Path.GetFullPath(AppPaths.NativeKnownHostsFilePath);
        string root = Path.GetFullPath(AppPaths.RootDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(root, fullPath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.StartsWith(root, fullPath, StringComparison.Ordinal);
        }

        Assert.EndsWith(Path.Combine("ssh", "native_known_hosts.json"), fullPath);
    }

    [Fact]
    public void MigrateLegacyRoot_CopiesEverythingExceptLogs_AndWritesMarker()
    {
        string temp = CreateTempDirectory();
        try
        {
            string legacy = Path.Combine(temp, "NovaTerminal");
            string fresh = Path.Combine(temp, "ntilde");
            Directory.CreateDirectory(Path.Combine(legacy, "themes"));
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"FontSize\":13}");
            File.WriteAllText(Path.Combine(legacy, "themes", "dark.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "logs", "debug.log"), "noise");

            bool migrated = AppPaths.MigrateLegacyRoot(legacy, fresh);

            Assert.True(migrated);
            Assert.Equal("{\"FontSize\":13}", File.ReadAllText(Path.Combine(fresh, "settings.json")));
            Assert.True(File.Exists(Path.Combine(fresh, "themes", "dark.json")));
            Assert.False(Directory.Exists(Path.Combine(fresh, "logs")));
            Assert.True(File.Exists(Path.Combine(fresh, AppPaths.MigrationMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyRoot_RunsOnce_MarkerBlocksSecondCopy()
    {
        string temp = CreateTempDirectory();
        try
        {
            string legacy = Path.Combine(temp, "NovaTerminal");
            string fresh = Path.Combine(temp, "ntilde");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "first");
            Assert.True(AppPaths.MigrateLegacyRoot(legacy, fresh));

            File.WriteAllText(Path.Combine(legacy, "settings.json"), "second");
            File.SetLastWriteTimeUtc(Path.Combine(legacy, "settings.json"), DateTime.UtcNow.AddMinutes(5));

            bool migratedAgain = AppPaths.MigrateLegacyRoot(legacy, fresh);

            Assert.False(migratedAgain);
            Assert.Equal("first", File.ReadAllText(Path.Combine(fresh, "settings.json")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyRoot_NoLegacyFolder_DoesNothing()
    {
        string temp = CreateTempDirectory();
        try
        {
            string fresh = Path.Combine(temp, "ntilde");

            bool migrated = AppPaths.MigrateLegacyRoot(Path.Combine(temp, "NovaTerminal"), fresh);

            Assert.False(migrated);
            Assert.False(Directory.Exists(fresh));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyRoot_SkipsAgentHostDiscoveryFile()
    {
        string temp = CreateTempDirectory();
        try
        {
            string legacy = Path.Combine(temp, "NovaTerminal");
            string fresh = Path.Combine(temp, "ntilde");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"FontSize\":13}");
            File.WriteAllText(
                Path.Combine(legacy, Ntilde.AgentHost.Contracts.AgentHostProtocol.DiscoveryFileName),
                "{\"pipeName\":\"stale\"}");

            bool migrated = AppPaths.MigrateLegacyRoot(legacy, fresh);

            Assert.True(migrated);
            Assert.Equal("{\"FontSize\":13}", File.ReadAllText(Path.Combine(fresh, "settings.json")));
            Assert.False(File.Exists(Path.Combine(fresh, Ntilde.AgentHost.Contracts.AgentHostProtocol.DiscoveryFileName)));
            Assert.True(File.Exists(Path.Combine(fresh, AppPaths.MigrationMarkerFileName)));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RootDirectory_FolderNameIsLowercaseNtilde()
    {
        string? previous = Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", null);
            Assert.Equal("ntilde", Path.GetFileName(AppPaths.RootDirectory));
            Assert.Equal("NovaTerminal", Path.GetFileName(AppPaths.LegacyRootDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", previous);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde_paths_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
