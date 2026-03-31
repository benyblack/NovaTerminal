using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.OpenSsh;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Ssh.Sessions;

public sealed class OpenSshSession : ITerminalSession
{
    private readonly ITerminalSession _inner;

    public static OpenSshSession FromDefaultStore(
        Guid profileId,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        ISshProfileStore store = new JsonSshProfileStore();
        ISshLaunchPlanner planner = new SshLaunchPlanner(store, new OpenSshConfigCompiler());
        return new OpenSshSession(profileId, planner, cols, rows, diagnosticsLevel, null, launcher, log);
    }

    public OpenSshSession(
        Guid profileId,
        ISshProfileStore profileStore,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ISshLaunchPlanner planner = new SshLaunchPlanner(profileStore, new OpenSshConfigCompiler());
        _inner = CreateInnerSession(profileId, planner, cols, rows, diagnosticsLevel, extraArgs, launcher, log);
    }

    public OpenSshSession(
        Guid profileId,
        ISshLaunchPlanner launchPlanner,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        ISshProcessLauncher? launcher = null,
        Action<string>? log = null)
    {
        _inner = CreateInnerSession(profileId, launchPlanner, cols, rows, diagnosticsLevel, extraArgs, launcher, log);
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
    public void AttachBuffer(TerminalBuffer buffer) => _inner.AttachBuffer(buffer);
    public void TakeSnapshot() => _inner.TakeSnapshot();
    public void Dispose() => _inner.Dispose();

    private static ITerminalSession CreateInnerSession(
        Guid profileId,
        ISshLaunchPlanner launchPlanner,
        int cols,
        int rows,
        SshDiagnosticsLevel diagnosticsLevel,
        IReadOnlyList<string>? extraArgs,
        ISshProcessLauncher? launcher,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(launchPlanner);

        var requestedExtraArgs = new List<string>();
        requestedExtraArgs.AddRange(diagnosticsLevel.ToArguments());
        if (extraArgs != null)
        {
            requestedExtraArgs.AddRange(extraArgs);
        }

        SshLaunchPlan launchPlan = launchPlanner.Plan(
            profileId,
            requestedExtraArgs.Count == 0 ? null : requestedExtraArgs);
        string args = SshArgBuilder.BuildCommandLine(launchPlan.Arguments);
        string safeArgs = SshArgBuilder.SanitizeForLog(args);

        Action<string> logger = log ?? Console.WriteLine;
        logger($"[OpenSshSession] Using OpenSSH executable: {launchPlan.SshExecutablePath}");
        logger($"[OpenSshSession] Arguments: {safeArgs}");
        logger($"[OpenSshSession] Generated config: {launchPlan.ConfigFilePath}");
        logger($"[OpenSshSession] Alias: {launchPlan.Alias}");
        logger($"[OpenSshSession] Diagnostics: {diagnosticsLevel}");

        return (launcher ?? new PtySshProcessLauncher())
            .Launch(launchPlan.SshExecutablePath, args, cols, rows, launchPlan.WorkingDirectory);
    }
}
