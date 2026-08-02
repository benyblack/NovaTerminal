using System;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Everything that turns a command the user ran into a history entry: the heuristic Enter-time
/// capture, the structured OSC 133 capture, the dedup between the two, secret redaction, and the
/// exit-code / duration patch that lands when the command finishes.
/// </summary>
/// <remarks>
/// <para>
/// There are two capture paths because there are two kinds of session. When the shell is
/// instrumented it tells us the command text it accepted, and that text is authoritative - it
/// survives multi-line input, history recall and line editing. When it is not, all we have is the
/// keystrokes we watched go by, so the Enter key is the trigger and the shadow query buffer is the
/// source.
/// </para>
/// <para>
/// The paths overlap for exactly one command. Structured capture only stands down the heuristic once
/// <see cref="AssistSessionContext.HasObservedStructuredCommandCaptureMarker"/> is set, and that only
/// becomes true when the first <c>CommandAccepted</c> arrives - which is after the first Enter. So
/// the first command of an instrumented session is captured twice unless something notices, and the
/// something is the pending-entry dedup below: an accepted command whose text matches the entry the
/// heuristic path just wrote is dropped, and the finish event patches the heuristic entry instead.
/// </para>
/// <para>
/// Capture is best-effort throughout. Every store call is wrapped: a history write must never take
/// down the keystroke that triggered it.
/// </para>
/// </remarks>
internal sealed class CapturePipeline
{
    private readonly IHistoryStore _historyStore;
    private readonly ISecretsFilter _secretsFilter;
    private readonly AssistSessionContext _context;

    private string? _pendingEntryId;
    private string? _pendingCommandText;

    public CapturePipeline(
        IHistoryStore historyStore,
        ISecretsFilter secretsFilter,
        AssistSessionContext context)
    {
        _historyStore = historyStore;
        _secretsFilter = secretsFilter;
        _context = context;
    }

    /// <summary>
    /// Heuristic capture: the user pressed Enter and we believe <paramref name="submission"/> is what
    /// went to the shell.
    /// </summary>
    /// <param name="submission">The shadow query buffer contents.</param>
    /// <param name="isSubmissionSuppressed">
    /// Whether the session marked this submission untrustworthy (pasted rather than typed).
    /// </param>
    public async Task CaptureSubmissionAsync(string submission, bool isSubmissionSuppressed)
    {
        try
        {
            string trimmed = submission.Trim();
            bool shouldPersist = !_context.IsAltScreenActive &&
                                 !_context.IsStructuredCaptureActive &&
                                 !isSubmissionSuppressed &&
                                 !string.IsNullOrWhiteSpace(trimmed) &&
                                 !trimmed.Contains('\n') &&
                                 !trimmed.Contains('\r');

            if (!shouldPersist)
            {
                return;
            }

            RedactionResult redaction = _secretsFilter.Redact(trimmed);
            var entry = new CommandHistoryEntry(
                Id: Guid.NewGuid().ToString("N"),
                CommandText: redaction.RedactedText,
                ExecutedAt: DateTimeOffset.UtcNow,
                ShellKind: _context.ShellKind ?? "unknown",
                WorkingDirectory: _context.WorkingDirectory,
                ProfileId: _context.ProfileId,
                SessionId: _context.SessionId,
                HostId: _context.HostId,
                ExitCode: null,
                IsRemote: _context.IsRemote,
                IsRedacted: redaction.WasRedacted,
                Source: CommandCaptureSource.Heuristic,
                DurationMs: null);

            await _historyStore.AppendAsync(entry);
            _pendingEntryId = entry.Id;
            _pendingCommandText = NormalizeCommandText(trimmed);
        }
        catch
        {
            // Assist capture is best-effort; Enter should still reach the shell path even if persistence fails.
        }
    }

    /// <summary>
    /// Patches the pending entry with an exit code observed outside shell integration (the host's own
    /// command-finished signal).
    /// </summary>
    public async Task CompleteSubmissionAsync(int? exitCode)
    {
        string? pendingEntryId = _pendingEntryId;
        _pendingEntryId = null;
        if (string.IsNullOrWhiteSpace(pendingEntryId))
        {
            return;
        }

        try
        {
            await _historyStore.TryUpdateExecutionResultAsync(pendingEntryId, exitCode, durationMs: null);
        }
        catch
        {
            // History metadata enrichment is best-effort only.
        }
    }

    /// <summary>
    /// Consumes a shell-integration event: records what it proves about the session, then runs the
    /// structured capture or the completion patch it implies.
    /// </summary>
    public async Task HandleShellIntegrationEventAsync(ShellIntegrationEvent shellEvent)
    {
        if (shellEvent.WorkingDirectory != null)
        {
            _context.SetWorkingDirectory(shellEvent.WorkingDirectory);
        }

        if (shellEvent.Type is ShellIntegrationEventType.PromptReady or
            ShellIntegrationEventType.CommandAccepted or
            ShellIntegrationEventType.CommandStarted or
            ShellIntegrationEventType.CommandFinished)
        {
            _context.ObserveShellIntegrationMarker();
        }

        if (shellEvent.Type is ShellIntegrationEventType.CommandAccepted)
        {
            _context.ObserveStructuredCommandCaptureMarker();
        }

        switch (shellEvent.Type)
        {
            case ShellIntegrationEventType.WorkingDirectoryChanged:
            case ShellIntegrationEventType.PromptReady:
            case ShellIntegrationEventType.CommandStarted:
                return;

            case ShellIntegrationEventType.CommandAccepted:
                await CaptureAcceptedCommandAsync(shellEvent);
                return;

            case ShellIntegrationEventType.CommandFinished:
                await CompleteAcceptedCommandAsync(shellEvent);
                return;
        }
    }

    private async Task CaptureAcceptedCommandAsync(ShellIntegrationEvent shellEvent)
    {
        if (!_context.IsShellIntegrationEnabled || _context.IsAltScreenActive)
        {
            return;
        }

        string commandText = shellEvent.CommandText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return;
        }

        // The first-command double-capture guard: the heuristic path already wrote this exact command
        // moments ago, so let its entry stand and let CommandFinished patch that one.
        string normalizedCommandText = NormalizeCommandText(commandText);
        if (!string.IsNullOrWhiteSpace(_pendingEntryId) &&
            string.Equals(_pendingCommandText, normalizedCommandText, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            RedactionResult redaction = _secretsFilter.Redact(commandText);
            var entry = new CommandHistoryEntry(
                Id: Guid.NewGuid().ToString("N"),
                CommandText: redaction.RedactedText,
                ExecutedAt: shellEvent.Timestamp,
                ShellKind: _context.ShellKind ?? "unknown",
                WorkingDirectory: shellEvent.WorkingDirectory ?? _context.WorkingDirectory,
                ProfileId: _context.ProfileId,
                SessionId: _context.SessionId,
                HostId: _context.HostId,
                ExitCode: null,
                IsRemote: _context.IsRemote,
                IsRedacted: redaction.WasRedacted,
                Source: CommandCaptureSource.ShellIntegration,
                DurationMs: null);

            await _historyStore.AppendAsync(entry);
            _pendingEntryId = entry.Id;
            _pendingCommandText = normalizedCommandText;
        }
        catch
        {
            // Structured capture is best-effort and must not affect shell execution.
        }
    }

    private async Task CompleteAcceptedCommandAsync(ShellIntegrationEvent shellEvent)
    {
        string? pendingEntryId = _pendingEntryId;
        if (string.IsNullOrWhiteSpace(pendingEntryId))
        {
            return;
        }

        long? durationMs = shellEvent.Duration.HasValue
            ? (long)Math.Round(shellEvent.Duration.Value.TotalMilliseconds)
            : null;

        try
        {
            await _historyStore.TryUpdateExecutionResultAsync(pendingEntryId, shellEvent.ExitCode, durationMs);
            _pendingEntryId = null;
            _pendingCommandText = null;
        }
        catch
        {
            // Structured metadata updates are best-effort only.
        }
    }

    private static string NormalizeCommandText(string commandText) => commandText.Trim();
}
