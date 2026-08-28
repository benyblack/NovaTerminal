using System.Runtime.InteropServices;
using NovaTerminal.Shell;

namespace NovaTerminal.Shots;

/// <summary>
/// The fictional machine every screenshot is taken on: an isolated NovaTerminal profile
/// root, a scratch workspace, and a shell profile that ignores the developer's dotfiles.
///
/// Setting NOVATERM_APPDATA_ROOT is load-bearing rather than tidy. MainWindow's constructor
/// calls TerminalSettings.Load(), and several ordinary actions call Save(), so without the
/// override a capture run would both read the developer's live settings into public images
/// and be able to rewrite them.
/// </summary>
public sealed class DemoWorld : IDisposable
{
    private const string RootOverrideEnvVar = "NOVATERM_APPDATA_ROOT";

    private readonly string _baseDirectory;
    private readonly string? _previousRootOverride;

    private DemoWorld(string baseDirectory, string? previousRootOverride)
    {
        _baseDirectory = baseDirectory;
        _previousRootOverride = previousRootOverride;
        ProfileRoot = Path.Combine(baseDirectory, "profile");
        WorkspaceRoot = Path.Combine(baseDirectory, "workspace", "nova-demo");
        DemoProfile = BuildDemoProfile(WorkspaceRoot);
    }

    public string ProfileRoot { get; }

    public string WorkspaceRoot { get; }

    public TerminalProfile DemoProfile { get; }

    public static DemoWorld Create(string baseDirectory)
    {
        string? previous = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        var world = new DemoWorld(baseDirectory, previous);

        Directory.CreateDirectory(world.ProfileRoot);
        Directory.CreateDirectory(world.WorkspaceRoot);
        Environment.SetEnvironmentVariable(RootOverrideEnvVar, world.ProfileRoot);
        CreateAppPathsScaffolding();

        return world;
    }

    /// <summary>
    /// Mirrors the directory scaffolding <see cref="AppPaths.EnsureInitialized"/> creates, because
    /// that method is gated by a private static bool that flips once per PROCESS, not once per
    /// root. Without this, only the first DemoWorld created in a process would get a ProfileRoot
    /// with themes/, logs/, sessions/, etc.; every DemoWorld created afterward in the same process
    /// (e.g. a second scenario run, or a second instance in a single test) would get a ProfileRoot
    /// containing nothing but settings.json, because EnsureInitialized already ran once and will
    /// not run its directory-creation body again.
    ///
    /// Pulled from AppPaths' own public path properties (which read NOVATERM_APPDATA_ROOT live)
    /// rather than a hardcoded list of literal folder names, so this cannot silently drift from
    /// what AppPaths.EnsureInitialized actually creates.
    /// </summary>
    private static void CreateAppPathsScaffolding()
    {
        foreach (string directory in new[]
        {
            AppPaths.ThemesDirectory,
            AppPaths.LogsDirectory,
            AppPaths.SessionsDirectory,
            AppPaths.WorkspacesDirectory,
            AppPaths.WorkspaceTemplatesDirectory,
            AppPaths.PolicyDirectory,
            AppPaths.RecordingsDirectory,
            AppPaths.CommandAssistDirectory,
            AppPaths.SshDirectory
        })
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static TerminalProfile BuildDemoProfile(string workspaceRoot)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        return new TerminalProfile
        {
            Name = "Demo",
            Command = windows ? "pwsh.exe" : "/bin/bash",
            Arguments = windows ? "-NoProfile -NoLogo" : "--noprofile --norc",
            StartingDirectory = workspaceRoot,
            Type = ConnectionType.Local
        };
    }

    /// <summary>
    /// Writes a settings.json into the isolated root with everything a screenshot depends on
    /// pinned. <paramref name="customize"/> lets a scenario change theme or tab orientation
    /// without touching anything else.
    /// </summary>
    public void SeedSettings(Action<TerminalSettings>? customize = null)
    {
        var settings = new TerminalSettings
        {
            ThemeName = "Dracula",
            FontFamily = "Cascadia Code PL, CaskaydiaCove Nerd Font, Cascadia Code, Consolas, Monospace",
            FontSize = 14
        };

        settings.Profiles.Clear();
        settings.Profiles.Add(DemoProfile);
        settings.DefaultProfileId = DemoProfile.Id;

        customize?.Invoke(settings);
        settings.Save();
    }

    private const string CommitDate = "2026-08-20T10:15:00+00:00";

    /// <summary>
    /// Lays down the demo project and a scripted git history. Author and committer identity
    /// and dates are fixed so `git log --graph` renders the same story on every run and on
    /// every machine.
    /// </summary>
    public void SeedWorkspace()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "scripts"));
        Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "src"));

        CopyAsset(assets, "nova-banner.sh", Path.Combine(WorkspaceRoot, "scripts", "nova-banner.sh"));
        CopyAsset(assets, "demo-test.sh", Path.Combine(WorkspaceRoot, "scripts", "demo-test.sh"));
        CopyAsset(assets, "sixel-decoder.rs", Path.Combine(WorkspaceRoot, "src", "sixel-decoder.rs"));

        Git("init --initial-branch=feat/sixel-decoder");
        Git("config user.name nova");
        Git("config user.email nova@demo");
        Git("add .");
        Commit("feat(vt): add sixel decoder skeleton");

        File.AppendAllText(Path.Combine(WorkspaceRoot, "src", "sixel-decoder.rs"),
            "\n// TODO: raster attributes\n");
        Git("add .");
        Commit("feat(vt): parse sixel raster attributes");

        File.WriteAllText(Path.Combine(WorkspaceRoot, "README.md"), "# nova-demo\n");
        Git("add .");
        Commit("docs: describe the decoder pipeline");
    }

    private static void CopyAsset(string assetsDirectory, string name, string destination)
    {
        File.Copy(Path.Combine(assetsDirectory, name), destination, overwrite: true);
    }

    private void Commit(string message) => Git($"commit -m \"{message}\"");

    private void Git(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["GIT_AUTHOR_DATE"] = CommitDate;
        psi.Environment["GIT_COMMITTER_DATE"] = CommitDate;

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootOverrideEnvVar, _previousRootOverride);

        try
        {
            if (Directory.Exists(_baseDirectory))
            {
                ClearReadOnlyAttributes(_baseDirectory);
                Directory.Delete(_baseDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A shell that has not fully exited can hold a handle in the workspace, or git's
            // read-only object files can still resist deletion. Leaving a temp directory behind
            // is a worse outcome than a failed run only in theory; make it visible rather than
            // throwing out of Dispose.
            Console.Error.WriteLine($"[shots] could not remove demo world at {_baseDirectory}");
        }
    }

    /// <summary>
    /// Git marks files under .git/objects/ (and packed refs) read-only on Windows. A plain
    /// recursive delete throws UnauthorizedAccessException the moment it hits one of those, so
    /// this clears the attribute on every file first.
    /// </summary>
    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
