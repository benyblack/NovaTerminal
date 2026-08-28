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

    /// <summary>
    /// The prompt every screenshot shows. A literal string rather than a shell expansion:
    /// <c>\u</c>, <c>\h</c> and <c>\w</c> would resolve to the developer's account, machine and
    /// temp path, which is exactly the leak this whole class exists to prevent. The branch name
    /// is the demo repository's real branch (see <see cref="SeedWorkspace"/>), so the prompt is
    /// not lying about anything the image also shows.
    ///
    /// It opens by setting the window title (OSC 0), which is where the tab label comes from:
    /// ConPTY titles a new pane with the child's full command line, so without this the tab strip
    /// reads "C:\Program Files\Git\bin\bash.exe" in every screenshot.
    /// </summary>
    private const string DemoPrompt =
        "\\[\\e]0;nova-demo\\a\\]" +
        "\\[\\e[32m\\]nova@demo \\[\\e[33m\\]~/projects/nova-demo \\[\\e[36m\\](feat/sixel-decoder)\\[\\e[0m\\] $ ";

    private readonly string _baseDirectory;
    private readonly Dictionary<string, string?> _environmentToRestore = [];

    private DemoWorld(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        ProfileRoot = Path.Combine(baseDirectory, "profile");
        WorkspaceRoot = Path.Combine(baseDirectory, "workspace", "nova-demo");
        HomeRoot = Path.Combine(baseDirectory, "home");
        DemoProfile = BuildDemoProfile(WorkspaceRoot);
    }

    public string ProfileRoot { get; }

    public string WorkspaceRoot { get; }

    /// <summary>The demo machine's home directory. Nothing of the developer's is reachable from it.</summary>
    public string HomeRoot { get; }

    public TerminalProfile DemoProfile { get; }

    public static DemoWorld Create(string baseDirectory)
    {
        var world = new DemoWorld(baseDirectory);

        Directory.CreateDirectory(world.ProfileRoot);
        Directory.CreateDirectory(world.WorkspaceRoot);
        Directory.CreateDirectory(world.HomeRoot);
        world.SetEnvironment(RootOverrideEnvVar, world.ProfileRoot);
        world.ApplyDemoEnvironment();
        CreateAppPathsScaffolding();

        return world;
    }

    /// <summary>
    /// Sets a process environment variable, remembering the value it displaces so
    /// <see cref="Dispose"/> can put it back.
    /// </summary>
    private void SetEnvironment(string name, string? value)
    {
        if (!_environmentToRestore.ContainsKey(name))
        {
            _environmentToRestore[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Gives the demo machine its identity, in the only place that can: the harness process's own
    /// environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TerminalProfile"/> carries no environment member and <c>ITerminalSession</c> takes
    /// no environment parameter, so the PTY child inherits this process's environment verbatim (the
    /// native spawn seeds its block from <c>std::env::vars()</c> and adds only TERM, COLORTERM and
    /// TERM_PROGRAM). Every variable that could reach a pixel therefore has to be set here, before
    /// any pane opens.
    /// </para>
    /// <para>
    /// <c>PS1</c> is the one that matters most. Left alone, the shell prints the developer's
    /// account, host and working directory into the hero image of a public README - the second
    /// motivation in the spec. It is honoured because the demo shell is started with
    /// <c>--norc</c>, so no system or user rc file runs to overwrite it; verified on this machine
    /// against Git for Windows' /etc/bash.bashrc, which otherwise sets the
    /// <c>user@host MINGW64 /path (branch)</c> prompt even when <c>--noprofile</c> is passed.
    /// </para>
    /// </remarks>
    private void ApplyDemoEnvironment()
    {
        SetEnvironment("HOME", HomeRoot);
        SetEnvironment("USERPROFILE", HomeRoot);
        SetEnvironment("PS1", DemoPrompt);
        SetEnvironment("TERM", "xterm-256color");
        SetEnvironment("LANG", "C.UTF-8");
        SetEnvironment("LC_ALL", "C.UTF-8");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Puts Git for Windows ahead of C:\Windows\System32\bash.exe, which is the WSL
            // launcher: a scenario that types `bash script.sh` would otherwise boot a Linux
            // distribution and resolve the workspace through /mnt/c, if it resolved it at all.
            // Taken from the demo profile's own command so the `bash` a scenario types is the same
            // binary the pane is already running.
            string gitBin = Path.GetDirectoryName(DemoProfile.Command)!;
            SetEnvironment("PATH", gitBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }
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

    /// <summary>
    /// The shell every screenshot is taken in: bash, with no profile and no rc file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// bash on Windows too, rather than pwsh, for two reasons. The scenarios run
    /// <c>bash scripts/nova-banner.sh</c>, which reads naturally in a marketing image and must
    /// not mean "boot WSL"; and the prompt the spec asks for
    /// (<c>nova@demo ~/projects/nova-demo (feat/sixel-decoder) $</c>) is a <c>PS1</c>, which an
    /// inherited environment variable can set outright. PowerShell has no equivalent lever from
    /// outside the process: with <c>-NoProfile</c> there is no hook to define <c>prompt</c> in, and
    /// redefining it from inside the session would replace the shell-integration wrapper.
    /// </para>
    /// <para>
    /// <c>--norc</c> is load-bearing, not tidiness. Without it Git for Windows' /etc/bash.bashrc
    /// runs even under <c>--noprofile</c> and sets the <c>user@host MINGW64 /path (branch)</c>
    /// prompt over the inherited <c>PS1</c>, putting the developer's account and paths in the
    /// image. It costs bash shell integration - the OSC 133 bootstrap is injected as
    /// <c>--rcfile</c>, which <c>--norc</c> inhibits - which no scenario needs yet. A later
    /// scenario that does need marks should anchor the prompt another way (Git for Windows sources
    /// <c>~/.config/git/git-prompt.sh</c> when it exists, and HOME is ours) rather than simply
    /// dropping <c>--norc</c>.
    /// </para>
    /// </remarks>
    private static TerminalProfile BuildDemoProfile(string workspaceRoot)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        return new TerminalProfile
        {
            Name = "Demo",
            Command = windows ? ResolveWindowsBash() : "/bin/bash",
            Arguments = "--noprofile --norc -i",
            StartingDirectory = workspaceRoot,
            Type = ConnectionType.Local
        };
    }

    /// <summary>
    /// Finds Git for Windows' bash.exe by way of git.exe on PATH.
    /// </summary>
    /// <remarks>
    /// Derived from git.exe rather than hardcoded to C:\Program Files\Git so a non-default install
    /// (scoop, a portable copy, a D: drive) still works. git.exe ships in several directories of
    /// one install - cmd\, mingw64\bin\, bin\ - so this walks up from wherever it was found until
    /// it reaches the directory that has bash.exe beneath it.
    ///
    /// Failing here is deliberate. The alternative, letting the profile keep the bare name
    /// <c>bash</c>, resolves on Windows to the WSL launcher in System32: the scenario would appear
    /// to work while running inside a Linux distribution that cannot see the demo workspace, and
    /// the first sign of it would be a wrong image.
    /// </remarks>
    private static string ResolveWindowsBash()
    {
        foreach (string gitExecutable in ExecutablesOnPath("git.exe"))
        {
            for (string? directory = Path.GetDirectoryName(gitExecutable);
                 directory is not null;
                 directory = Path.GetDirectoryName(directory))
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(directory, "bin", "bash.exe"),
                    Path.Combine(directory, "usr", "bin", "bash.exe")
                })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "Could not find Git for Windows' bash.exe by walking up from git.exe on PATH. The " +
            "screenshot scenarios run `bash scripts/...`, and on Windows a bare `bash` resolves to " +
            "the WSL launcher in System32, which would run them inside a Linux distribution instead " +
            "of the demo workspace. Install Git for Windows, or put its git.exe on PATH.");
    }

    private static IEnumerable<string> ExecutablesOnPath(string fileName)
    {
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string directory in directories)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), fileName);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing the whole search over.
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Writes a settings.json into the isolated root with everything a screenshot depends on
    /// pinned. <paramref name="customize"/> lets a scenario change theme or tab orientation
    /// without touching anything else.
    /// </summary>
    public void SeedSettings(Action<TerminalSettings>? customize = null)
    {
        // The first family in the list is not just a preference: TerminalView measures the cell
        // from FontFamily.Name, which is the first name, and falls back to some other monospace
        // face only for drawing. When the first name is not installed the two disagree and every
        // glyph sits in a cell wider than itself - the text in the first captured hero still was
        // visibly letter-spaced for exactly that reason ("Cascadia Code PL" is a Nerd-Font-patched
        // build almost nobody has). So the list now leads with faces that ship with Windows 11 and
        // with mainstream Linux, and keeps the patched ones as later preferences.
        var settings = new TerminalSettings
        {
            ThemeName = "Dracula",
            FontFamily = "Cascadia Mono, Cascadia Code, DejaVu Sans Mono, Consolas, Monospace",

            // Larger than a working font, because a screenshot is looked at rather than worked in.
            // These PNGs are captured at 1280x800 and then displayed far smaller - inline in a
            // README, in a docs page, as a gallery thumbnail - and at 14 the transcript stops being
            // readable well before the image stops being shown. 18 survives that scaling, and it is
            // what presenters and screencasts use for the same reason. Every scenario inherits it.
            FontSize = 18
        };

        settings.Profiles.Clear();
        settings.Profiles.Add(DemoProfile);
        settings.DefaultProfileId = DemoProfile.Id;

        customize?.Invoke(settings);
        settings.Save();
    }

    /// <summary>
    /// Deletes the session file the previous scenario's window saved on its way out.
    /// </summary>
    /// <remarks>
    /// MainWindow saves the open tabs during teardown (PerformAppTeardown) and restores them on
    /// the next start (TryRestoreStartupSession), which is right for the application and wrong for
    /// a capture run: every scenario after the first would open holding the previous scenario's
    /// tabs instead of a clean window, so what the image showed would depend on what ran before
    /// it. The save lands inside this world's isolated profile root, so this is the whole of the
    /// state to clear.
    /// </remarks>
    public void ForgetPreviousSession()
    {
        // AppPaths resolves live from NOVATERM_APPDATA_ROOT, so this is normally this world's own
        // session file. Checked rather than assumed, because the one way it could be anything else
        // is the override having been lost - and then this would be deleting the developer's real
        // saved session, which is the accident this class exists to make impossible.
        string sessionFile = AppPaths.SessionFilePath;
        if (!sessionFile.StartsWith(ProfileRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete '{sessionFile}': it is outside the demo world at '{ProfileRoot}', " +
                "so NOVATERM_APPDATA_ROOT is no longer pointing at this world.");
        }

        if (File.Exists(sessionFile))
        {
            File.Delete(sessionFile);
        }
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

        LeaveWorkInProgress();
    }

    /// <summary>
    /// Leaves the demo checkout mid-change: two edited files and one new one.
    /// </summary>
    /// <remarks>
    /// A committed-clean tree makes <c>git status --short --branch</c> print a single line, which
    /// is both an unconvincing screenshot - nobody's terminal looks like that - and, in the hero
    /// still, three empty rows where the story should be. These edits are the smallest thing that
    /// makes the status output look like a real working session.
    /// </remarks>
    private void LeaveWorkInProgress()
    {
        File.AppendAllText(
            Path.Combine(WorkspaceRoot, "src", "sixel-decoder.rs"),
            "\nfn decode_raster_attributes(_input: &[u8]) -> Option<RasterAttributes> {\n    None\n}\n");

        File.AppendAllText(
            Path.Combine(WorkspaceRoot, "README.md"),
            "\nA worked example of the sixel decoder pipeline.\n");

        File.WriteAllText(
            Path.Combine(WorkspaceRoot, "src", "sixel-palette.rs"),
            "//! Colour registers for the sixel decoder.\n\npub struct Palette;\n");
    }

    private static void CopyAsset(string assetsDirectory, string name, string destination)
    {
        File.Copy(Path.Combine(assetsDirectory, name), destination, overwrite: true);
    }

    private void Commit(string message) => Git($"commit -m \"{message}\"");

    // Deliberate deviation from the brief's original synchronous shape: that version called
    // WaitForExit() before reading either redirected stream, and only ever read StandardError
    // (never StandardOutput). Both streams share a fixed-size OS pipe buffer; if a git invocation
    // ever writes enough combined output to fill it before exiting (autocrlf warnings, a global
    // hook, hint/advice text), the child blocks on write() and WaitForExit() never returns - the
    // exact class of redirected-stdout deadlock CLAUDE.md's build-wrapper rule exists to avoid.
    // Do not "restore" the synchronous version; drain both streams asynchronously instead.
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

        var stderr = new System.Text.StringBuilder();

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start git.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with {process.ExitCode}: {stderr}");
        }
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _environmentToRestore)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _environmentToRestore.Clear();

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
