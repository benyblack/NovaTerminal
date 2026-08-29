using NovaTerminal.Shell;
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

    // Task 7 seeds an observe-on/act-off agent-access baseline inside SeedSettings itself, before
    // it invokes customize - specifically so a scenario's own Settings override (Task 8's
    // agent-access-act shot flips AgentAccessActEnabled back on) wins. If the baseline ran after
    // customize instead - or customize were dropped entirely - this override would be silently
    // discarded and that shot would render act OFF despite asking for it ON, with no failing test
    // to catch it.
    [Fact]
    public void SeedSettings_LetsCustomizeOverrideTheAgentAccessBaseline()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedSettings(settings => settings.AgentAccessActEnabled = true);

        TerminalSettings loaded = TerminalSettings.Load();
        Assert.True(loaded.AgentAccessObserveEnabled, "Expected the baseline's observe-on default to survive.");
        Assert.True(loaded.AgentAccessActEnabled, "Expected customize's act-on override to win over the baseline.");
    }

    // nova-banner.sh's agent dots read NOVA_SHOTS_AGENT_OBSERVE_ON / NOVA_SHOTS_AGENT_ACT_ON
    // instead of hardcoding "observe on, act off" - the fix for a real defect where the banner
    // contradicted the very scenario capturing it (AgentSessionScenario and ClipAgentScenario
    // both flip act on, then show the banner claiming it's off). This pins the derivation at the
    // source: whatever SeedSettings actually wrote - baseline or customize's override - must be
    // what these two variables report, exported after customize so an override always wins.
    [Fact]
    public void SeedSettings_ExportsTheBaselineAgentAccessAsEnvironmentVariables()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedSettings();

        Assert.Equal("1", Environment.GetEnvironmentVariable("NOVA_SHOTS_AGENT_OBSERVE_ON"));
        Assert.Equal("0", Environment.GetEnvironmentVariable("NOVA_SHOTS_AGENT_ACT_ON"));
    }

    [Fact]
    public void SeedSettings_ExportsCustomizesAgentAccessOverride_NotJustTheBaseline()
    {
        using var world = DemoWorld.Create(NewBaseDir());

        world.SeedSettings(settings => settings.AgentAccessActEnabled = true);

        Assert.Equal("1", Environment.GetEnvironmentVariable("NOVA_SHOTS_AGENT_OBSERVE_ON"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("NOVA_SHOTS_AGENT_ACT_ON"));
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

    // Every scenario after the first opens a window that would otherwise restore the previous
    // scenario's tabs, because MainWindow saves them during teardown. Verified end to end as well:
    // before this existed, a two-scenario run failed on the second scenario with its restored
    // pane's shell exiting immediately.
    [Fact]
    public void ForgetPreviousSession_RemovesTheSavedSessionInsideTheDemoWorld()
    {
        using var world = DemoWorld.Create(NewBaseDir());
        string sessionFile = AppPaths.SessionFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        File.WriteAllText(sessionFile, "{}");
        Assert.StartsWith(world.ProfileRoot, sessionFile, StringComparison.OrdinalIgnoreCase);

        world.ForgetPreviousSession();

        Assert.False(File.Exists(sessionFile), $"Expected {sessionFile} to be gone.");
    }

    [Fact]
    public void ForgetPreviousSession_RefusesToDeleteOutsideTheDemoWorld()
    {
        using var world = DemoWorld.Create(NewBaseDir());
        string? isolatedRoot = Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        try
        {
            // Whatever this now resolves to, it is somebody else's - very possibly the developer's
            // own saved session.
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", Path.Combine(Path.GetTempPath(), "not-a-demo-world"));

            Assert.Throws<InvalidOperationException>(world.ForgetPreviousSession);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", isolatedRoot);
        }
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
