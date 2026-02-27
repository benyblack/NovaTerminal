using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Ssh.Sessions;

public interface ISshProcessLauncher
{
    ITerminalSession Launch(string executablePath, string arguments, int cols, int rows, string? cwd);
}

public sealed class PtySshProcessLauncher : ISshProcessLauncher
{
    public ITerminalSession Launch(string executablePath, string arguments, int cols, int rows, string? cwd)
    {
        return new RustPtySession(executablePath, cols, rows, arguments, cwd);
    }
}

public sealed class SshSession : ITerminalSession
{
    private readonly ITerminalSession _inner;

    public static SshSession FromDefaultStore(
        Guid profileId,
        int cols = 120,
        int rows = 30,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        return new SshSession(profileId, new JsonSshProfileStore(), cols, rows, launcher, log);
    }

    public SshSession(
        Guid profileId,
        ISshProfileStore profileStore,
        int cols = 120,
        int rows = 30,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(profileStore);

        SshProfile profile = profileStore.GetProfile(profileId)
            ?? throw new InvalidOperationException($"SSH profile '{profileId}' was not found.");

        string sshPath = ResolveSshExecutablePath();
        string args = SshArgBuilder.BuildCommandLine(profile);
        string safeArgs = SshArgBuilder.SanitizeForLog(args);

        Action<string> logger = log ?? Console.WriteLine;
        logger($"[SshSession] Using OpenSSH executable: {sshPath}");
        logger($"[SshSession] Arguments: {safeArgs}");

        // Keep launch behind an abstraction so we can swap to native SSH in the future
        // without changing the session surface used by the rest of the app.
        _inner = (launcher ?? new PtySshProcessLauncher())
            .Launch(sshPath, args, cols, rows, string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? null : profile.WorkingDirectory);
    }

    public Guid Id => _inner.Id;
    public string ShellCommand => _inner.ShellCommand;
    public bool IsProcessRunning => _inner.IsProcessRunning;
    public bool HasActiveChildProcesses => _inner.HasActiveChildProcesses;
    public int? ExitCode => _inner.ExitCode;
    public bool IsRecording => _inner.IsRecording;

    public event Action<string>? OnOutputReceived
    {
        add => _inner.OnOutputReceived += value;
        remove => _inner.OnOutputReceived -= value;
    }

    public event Action<int>? OnExit
    {
        add => _inner.OnExit += value;
        remove => _inner.OnExit -= value;
    }

    public void SendInput(string input) => _inner.SendInput(input);
    public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
    public void StartRecording(string filePath) => _inner.StartRecording(filePath);
    public void StopRecording() => _inner.StopRecording();
    public void AttachBuffer(TerminalBuffer buffer) => _inner.AttachBuffer(buffer);
    public void TakeSnapshot() => _inner.TakeSnapshot();
    public void Dispose() => _inner.Dispose();

    private static string ResolveSshExecutablePath()
    {
        if (TryFindInPath(out string? pathFromPath))
        {
            return pathFromPath ?? throw new InvalidOperationException("PATH lookup returned no executable path.");
        }

        if (OperatingSystem.IsWindows())
        {
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string fallbackPath = string.IsNullOrWhiteSpace(windowsDir)
                ? @"C:\Windows\System32\OpenSSH\ssh.exe"
                : Path.Combine(windowsDir, "System32", "OpenSSH", "ssh.exe");

            if (File.Exists(fallbackPath))
            {
                return fallbackPath;
            }
        }

        throw new FileNotFoundException("Unable to locate system OpenSSH executable (ssh).");
    }

    private static bool TryFindInPath(out string? resolvedPath)
    {
        string? envPath = Environment.GetEnvironmentVariable("PATH");
        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(envPath))
        {
            return false;
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "ssh.exe", "ssh" }
            : new[] { "ssh" };

        foreach (string dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                string fullPath = Path.Combine(trimmed, candidate);
                if (File.Exists(fullPath))
                {
                    resolvedPath = fullPath;
                    return true;
                }
            }
        }

        return false;
    }
}
