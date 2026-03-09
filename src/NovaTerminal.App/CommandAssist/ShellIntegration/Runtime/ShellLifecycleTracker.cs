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
        Emit(ShellIntegrationEventType.CommandAccepted, commandText, exitCode: null, duration: null);
    }

    public void HandleCommandStarted()
    {
        _commandStartedAt = _nowProvider();
        Emit(ShellIntegrationEventType.CommandStarted, commandText: null, exitCode: null, duration: null);
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
        DateTimeOffset? timestamp = null)
    {
        EventObserved?.Invoke(new ShellIntegrationEvent(
            Type: type,
            Timestamp: timestamp ?? _nowProvider(),
            CommandText: commandText,
            WorkingDirectory: _workingDirectory,
            ExitCode: exitCode,
            Duration: duration));
    }
}
