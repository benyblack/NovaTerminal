using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.Platform.Ssh.Storage;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Platform.Ssh.Sessions;

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
    private readonly OpenSshSession _inner;

    public static SshSession FromDefaultStore(
        Guid profileId,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        return new SshSession(profileId, new JsonSshProfileStore(), cols, rows, diagnosticsLevel, null, launcher, log);
    }

    public SshSession(
        Guid profileId,
        ISshProfileStore profileStore,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        _inner = new OpenSshSession(profileId, profileStore, cols, rows, diagnosticsLevel, extraArgs, launcher, log);
    }

    public SshSession(
        Guid profileId,
        ISshLaunchPlanner launchPlanner,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        _inner = new OpenSshSession(profileId, launchPlanner, cols, rows, diagnosticsLevel, extraArgs, launcher, log);
    }

    public Guid Id => _inner.Id;
    public string ShellCommand => _inner.ShellCommand;
    public string? ShellArguments => _inner.ShellArguments;
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
    public bool IsFlightRecording => _inner.IsFlightRecording;
    public void EnableFlightRecording(long maxTotalBytes) => _inner.EnableFlightRecording(maxTotalBytes);
    public void DisableFlightRecording() => _inner.DisableFlightRecording();
    public bool TryExportFlightRecording(string filePath, out NovaTerminal.Replay.FlightExportInfo info) => _inner.TryExportFlightRecording(filePath, out info);
    public void Dispose() => _inner.Dispose();
}
