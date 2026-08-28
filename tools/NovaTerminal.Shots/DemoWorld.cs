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

        return world;
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
