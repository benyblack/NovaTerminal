using System;
using System.Collections.Generic;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Shared <see cref="NovaTerminal.Pty.ITerminalSession"/> stub for act-surface
/// and attention-signal protocol tests: a minimal input-accepting session that
/// records what was sent and can be told to report itself as not running.
/// </summary>
internal sealed class InputStubSession : NovaTerminal.Pty.ITerminalSession
{
    private readonly bool _running;
    public InputStubSession(bool running = true) => _running = running;

    public readonly List<string> Inputs = new();

    public void SendInput(string input) => Inputs.Add(input);
    public bool IsProcessRunning => _running;

    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand => "stub";
    public string? ShellArguments => null;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode => null;
    public bool IsRecording => false;
    public event Action<string>? OnOutputReceived { add { } remove { } }
    public event Action<int>? OnExit { add { } remove { } }
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public bool IsFlightRecording => false;
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out NovaTerminal.Replay.FlightExportInfo info) { info = default; return false; }
    public void Dispose() { }
}
