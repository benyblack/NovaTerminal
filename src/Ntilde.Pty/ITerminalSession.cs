using System;
using NovaTerminal.Replay;

namespace NovaTerminal.Pty
{
    /// <summary>Raw input/output for a terminal session.</summary>
    public interface ITerminalIO
    {
        void SendInput(string input);
        event Action<string>? OnOutputReceived;
    }

    /// <summary>Session lifetime: identity, resize, process state.</summary>
    public interface ITerminalLifecycle : IDisposable
    {
        Guid Id { get; }
        void Resize(int cols, int rows);
        bool IsProcessRunning { get; }
        bool HasActiveChildProcesses { get; }
        int? ExitCode { get; }
        event Action<int>? OnExit;
    }

    /// <summary>Descriptive metadata about the process backing this session.</summary>
    public interface ITerminalShellMetadata
    {
        string ShellCommand { get; }
        string? ShellArguments { get; }
    }

    /// <summary>Replay/recording control for a session's byte stream.</summary>
    public interface ITerminalRecorder
    {
        bool IsRecording { get; }
        void StartRecording(string filePath);
        void StopRecording();
    }

    /// <summary>
    /// Bounded in-memory "flight recorder" of a session's recent raw output and
    /// resizes, exportable on demand as a standard replay v2 file. Unlike
    /// <see cref="ITerminalRecorder"/>, nothing touches disk until an export is
    /// requested, and the retained window never includes input events — exports can
    /// be triggered by agents and must not exfiltrate typed secrets (see
    /// docs/plans/2026-07-07-agent-host-a4-replay-design.md).
    /// </summary>
    public interface ITerminalFlightRecorder
    {
        bool IsFlightRecording { get; }

        /// <summary>
        /// Starts retaining recent output/resize events, bounded by
        /// <paramref name="maxTotalBytes"/> (payload bytes plus a fixed per-event
        /// overhead). Idempotent while already enabled.
        /// </summary>
        void EnableFlightRecording(long maxTotalBytes);

        /// <summary>Stops retaining events and discards the current window. Idempotent.</summary>
        void DisableFlightRecording();

        /// <summary>
        /// Writes the retained window to <paramref name="filePath"/> as a replay v2
        /// file. Returns false (without touching disk) when flight recording is not
        /// enabled.
        /// </summary>
        bool TryExportFlightRecording(string filePath, out FlightExportInfo info);
    }

    /// <summary>
    /// Composite contract preserved for backward compatibility. New code should depend on
    /// the narrowest sub-interface it actually needs.
    ///
    /// AttachBuffer(TerminalBuffer) and TakeSnapshot() have been removed: they coupled the
    /// Pty layer to VT and they were vestigial in practice - AttachBuffer was registration-only
    /// (consumers held the buffer ref elsewhere) and TakeSnapshot was never called externally.
    /// Recording continues to work; it just no longer captures a buffer snapshot at start/stop.
    /// See arch test Pty_must_not_depend_on_Vt and Phase 5 of the architecture-foundation plan.
    /// </summary>
    public interface ITerminalSession
        : ITerminalIO, ITerminalLifecycle, ITerminalShellMetadata, ITerminalRecorder, ITerminalFlightRecorder
    {
    }
}
