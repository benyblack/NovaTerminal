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

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootOverrideEnvVar, _previousRootOverride);

        try
        {
            if (Directory.Exists(_baseDirectory))
            {
                Directory.Delete(_baseDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A shell that has not fully exited can hold a handle in the workspace. Leaving a
            // temp directory behind is a worse outcome than a failed run only in theory; make
            // it visible rather than throwing out of Dispose.
            Console.Error.WriteLine($"[shots] could not remove demo world at {_baseDirectory}");
        }
    }
}
