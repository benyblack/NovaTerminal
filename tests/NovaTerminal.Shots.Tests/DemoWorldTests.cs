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

    [Fact]
    public void SeedWorkspace_CreatesAGitRepoOnTheDemoBranch()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedWorkspace();

        Assert.True(Directory.Exists(Path.Combine(world.WorkspaceRoot, ".git")));
        Assert.True(File.Exists(Path.Combine(world.WorkspaceRoot, "scripts", "nova-banner.sh")));
        Assert.True(File.Exists(Path.Combine(world.WorkspaceRoot, "src", "sixel-decoder.rs")));

        string head = File.ReadAllText(Path.Combine(world.WorkspaceRoot, ".git", "HEAD"));
        Assert.Contains("feat/sixel-decoder", head, StringComparison.Ordinal);
    }

    // Git marks files under .git/objects/ read-only on Windows, so a seeded workspace is the one
    // scenario that actually exercises DemoWorld's ClearReadOnlyAttributes fix in Dispose(). The
    // other Dispose test (Dispose_RemovesEverythingItCreated) never calls SeedWorkspace, so it
    // would keep passing even if that fix regressed. Without this test, a regression here would
    // be silent: Dispose() only writes to Console.Error and never rethrows, so seeded workspaces
    // would just accumulate on disk with no failing test to catch it.
    [Fact]
    public void Dispose_RemovesASeededGitWorkspace_EvenThoughGitMarksItsObjectsReadOnly()
    {
        string baseDir = NewBaseDir();
        var world = DemoWorld.Create(baseDir);
        world.SeedWorkspace();

        world.Dispose();

        Assert.False(Directory.Exists(baseDir), "DemoWorld left a seeded git workspace behind after disposal.");
    }

    // The PTY child inherits the harness process's environment verbatim - TerminalProfile has no
    // environment member - so these variables are the only thing standing between the developer's
    // account, home directory and shell prompt and a public marketing image.
    [Fact]
    public void Create_PointsTheShellEnvironmentAtTheDemoMachine()
    {
        string baseDir = NewBaseDir();
        using var world = DemoWorld.Create(baseDir);

        Assert.Equal(world.HomeRoot, Environment.GetEnvironmentVariable("HOME"));
        Assert.Equal(world.HomeRoot, Environment.GetEnvironmentVariable("USERPROFILE"));
        Assert.StartsWith(baseDir, world.HomeRoot, StringComparison.Ordinal);
        Assert.True(Directory.Exists(world.HomeRoot), $"Expected a demo home at {world.HomeRoot}.");

        // Everything bash wraps in \[ \] is non-printing (colour changes, the window title), so
        // stripping those groups leaves exactly the characters the prompt puts on screen.
        string prompt = Environment.GetEnvironmentVariable("PS1") ?? string.Empty;
        string rendered = System.Text.RegularExpressions.Regex.Replace(prompt, @"\\\[.*?\\\]", string.Empty);
        Assert.Equal("nova@demo ~/projects/nova-demo (feat/sixel-decoder) $ ", rendered);

        // The prompt escapes that would print the real account, machine and working directory.
        Assert.DoesNotContain(@"\u", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\h", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\w", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_RestoresEveryEnvironmentVariableItChanged()
    {
        string? homeBefore = Environment.GetEnvironmentVariable("HOME");
        string? promptBefore = Environment.GetEnvironmentVariable("PS1");
        string? pathBefore = Environment.GetEnvironmentVariable("PATH");

        DemoWorld.Create(NewBaseDir()).Dispose();

        Assert.Equal(homeBefore, Environment.GetEnvironmentVariable("HOME"));
        Assert.Equal(promptBefore, Environment.GetEnvironmentVariable("PS1"));
        Assert.Equal(pathBefore, Environment.GetEnvironmentVariable("PATH"));
    }
}
