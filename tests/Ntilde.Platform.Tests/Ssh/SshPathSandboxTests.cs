using NovaTerminal.Platform;
using NovaTerminal.Platform.Ssh.Storage;

namespace NovaTerminal.Platform.Tests.Ssh;

/// <summary>
/// Regression tests for #406: everything <c>NovaTerminal.Platform</c> writes under the app-data
/// root must move when <c>NOVATERM_APPDATA_ROOT</c> moves.
/// </summary>
/// <remarks>
/// <para>
/// <c>JsonSshProfileStore</c> and <c>OpenSshConfigCompiler</c> each resolved
/// <c>%LOCALAPPDATA%\NovaTerminal\ssh</c> themselves and neither consulted the override, so a
/// redirected process — a test, a portable install, the screenshot harness — got a correctly
/// sandboxed settings file, log directory and session store, and then reached straight past the
/// sandbox for SSH data. It was found because a capture run wrote a fictional profile into a real
/// machine's profiles.json.
/// </para>
/// <para>
/// Serialized: these mutate a process-wide environment variable, so they cannot run beside anything
/// that reads it.
/// </para>
/// </remarks>
[Collection(nameof(SshPathSandboxCollection))]
public sealed class SshPathSandboxTests
{
    private const string RootOverrideEnvVar = "NOVATERM_APPDATA_ROOT";

    [Fact]
    public void Profile_store_default_path_follows_the_appdata_root_override()
    {
        using var root = new TemporaryRoot();

        string path = JsonSshProfileStore.GetDefaultStorePath();

        Assert.StartsWith(root.Path, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root.Path, "ssh", "profiles.json"), path);
    }

    [Fact]
    public void Ssh_directory_follows_the_appdata_root_override()
    {
        using var root = new TemporaryRoot();

        Assert.Equal(Path.Combine(root.Path, "ssh"), PlatformAppPaths.SshDirectory);
    }

    /// <summary>
    /// The property the whole issue reduces to, asserted directly rather than only through the two
    /// call sites: no path handed out while the override is set may sit under the real per-machine
    /// app-data directory.
    /// </summary>
    [Fact]
    public void No_platform_ssh_path_escapes_to_the_real_appdata_directory()
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaTerminal");

        using var root = new TemporaryRoot();

        foreach (string path in new[]
                 {
                     PlatformAppPaths.RootDirectory,
                     PlatformAppPaths.SshDirectory,
                     JsonSshProfileStore.GetDefaultStorePath(),
                 })
        {
            Assert.False(
                path.StartsWith(real, StringComparison.OrdinalIgnoreCase),
                $"'{path}' is inside the machine's real app-data directory '{real}' even though " +
                $"{RootOverrideEnvVar} redirects it to '{root.Path}'.");
        }
    }

    [Fact]
    public void Without_the_override_the_default_root_is_the_real_appdata_directory()
    {
        string? saved = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        Environment.SetEnvironmentVariable(RootOverrideEnvVar, null);
        try
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NovaTerminal");

            Assert.Equal(expected, PlatformAppPaths.RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RootOverrideEnvVar, saved);
        }
    }

    /// <summary>
    /// Points <c>NOVATERM_APPDATA_ROOT</c> at a fresh directory and restores whatever was there
    /// before, so a developer's or CI's own override survives the test.
    /// </summary>
    private sealed class TemporaryRoot : IDisposable
    {
        private readonly string? _saved;

        public TemporaryRoot()
        {
            _saved = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nova-ssh-sandbox-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable(RootOverrideEnvVar, Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(RootOverrideEnvVar, _saved);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }
}
