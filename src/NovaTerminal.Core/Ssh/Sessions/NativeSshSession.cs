using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Sessions;

public sealed class NativeSshSession : ITerminalSession
{
    public NativeSshSession(
        SshProfile profile,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = cols;
        _ = rows;
        _ = diagnosticsLevel;
        _ = extraArgs;
        _ = log;

        throw new NotSupportedException("Native SSH backend is not implemented yet.");
    }

    public Guid Id => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public string ShellCommand => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public string? ShellArguments => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public bool IsProcessRunning => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public bool HasActiveChildProcesses => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public int? ExitCode => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public bool IsRecording => throw new NotSupportedException("Native SSH backend is not implemented yet.");

    public event Action<string>? OnOutputReceived
    {
        add => throw new NotSupportedException("Native SSH backend is not implemented yet.");
        remove => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    }

    public event Action<int>? OnExit
    {
        add => throw new NotSupportedException("Native SSH backend is not implemented yet.");
        remove => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    }

    public void SendInput(string input) => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public void Resize(int cols, int rows) => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public void StartRecording(string filePath) => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public void StopRecording() => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public void AttachBuffer(TerminalBuffer buffer) => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public void TakeSnapshot() => throw new NotSupportedException("Native SSH backend is not implemented yet.");
    public void Dispose()
    {
    }
}
