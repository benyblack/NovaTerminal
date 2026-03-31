using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class AppPathsTests
{
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

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_paths_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
