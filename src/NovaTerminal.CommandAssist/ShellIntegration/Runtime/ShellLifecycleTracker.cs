using System;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.CommandAssist.ShellIntegration.Runtime;

public sealed class ShellLifecycleTracker
{
    private readonly Func<DateTimeOffset> _nowProvider;
    private string? _workingDirectory;
    private DateTimeOffset? _commandStartedAt;

    public ShellLifecycleTracker(Func<DateTimeOffset>? nowProvider = null)
    {
        _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<ShellIntegrationEvent>? EventObserved;

    public void HandleWorkingDirectoryChanged(string? workingDirectory)
    {
        _workingDirectory = workingDirectory;
        Emit(ShellIntegrationEventType.WorkingDirectoryChanged, commandText: null, exitCode: null, duration: null);
    }

    public void HandlePromptReady()
    {
        Emit(ShellIntegrationEventType.PromptReady, commandText: null, exitCode: null, duration: null);
    }

    public void HandleCommandAccepted(string? commandText)
    {
        // OSC 133;C is the real execution-start edge, so it (re)starts the duration clock used
        // as a fallback when the shell's D marker carries no duration. Without this, the clock
        // would be left at the 133;B timestamp — the moment the prompt finished printing — and
        // the fallback would bill the user's typing time to the command.
        _commandStartedAt = _nowProvider();
        Emit(ShellIntegrationEventType.CommandAccepted, commandText, exitCode: null, duration: null);
    }

    /// <summary>
    /// OSC 133;B: the prompt has finished printing and the shell handed the line editor to the
    /// user. <paramref name="markPosition"/> is where that boundary landed in the buffer, i.e.
    /// the first cell of the command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fires once per prompt (and again on every prompt repaint), not once per command.
    /// </para>
    /// <para>
    /// Deliberately does not touch <c>_commandStartedAt</c>. B is the moment the prompt
    /// finished printing, so a duration measured from it would bill the user's typing time
    /// to the command. Every path that can reach a D marker passes through
    /// <see cref="HandleCommandAccepted"/> (OSC 133;C) first, which is the real
    /// execution-start edge and sets the fallback clock there.
    /// </para>
    /// </remarks>
    public void HandleCommandStarted(ShellMarkPosition? markPosition = null)
    {
        Emit(
            ShellIntegrationEventType.CommandStarted,
            commandText: null,
            exitCode: null,
            duration: null,
            markPosition: markPosition);
    }

    public void HandleCommandFinished(int? exitCode, long? durationMs = null)
    {
        TimeSpan? duration = null;
        DateTimeOffset now = _nowProvider();
        if (durationMs.HasValue)
        {
            duration = TimeSpan.FromMilliseconds(durationMs.Value);
        }
        else if (_commandStartedAt.HasValue)
        {
            duration = now - _commandStartedAt.Value;
        }

        _commandStartedAt = null;
        Emit(ShellIntegrationEventType.CommandFinished, commandText: null, exitCode, duration, now);
    }

    private void Emit(
        ShellIntegrationEventType type,
        string? commandText,
        int? exitCode,
        TimeSpan? duration,
        DateTimeOffset? timestamp = null,
        ShellMarkPosition? markPosition = null)
    {
        EventObserved?.Invoke(new ShellIntegrationEvent(
            Type: type,
            Timestamp: timestamp ?? _nowProvider(),
            CommandText: commandText,
            WorkingDirectory: _workingDirectory,
            ExitCode: exitCode,
            Duration: duration,
            MarkPosition: markPosition));
    }
}
