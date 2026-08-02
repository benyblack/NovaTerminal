using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// The environment an assist session runs in: which shell, where, whether the connection is remote,
/// whether the alt screen is up, and how much of the OSC 133 contract the shell has actually proved
/// it speaks.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of the split described on <see cref="AssistSessionStateMachine"/>. None of
/// these values say anything about what the assist surface is doing - they describe the terminal
/// underneath it. They change on their own schedule (a directory change, an alt-screen switch, a
/// marker arriving) rather than in response to the user driving the assist UI, which is exactly why
/// they are not states of <see cref="AssistSessionState"/>.
/// </para>
/// <para>
/// Mutable and shared: the controller, the <see cref="CapturePipeline"/> and the
/// <see cref="SuggestionOrchestrator"/> all read the same instance, so a directory change observed
/// by a shell-integration event is immediately visible to the next ranking pass. All mutation goes
/// through named methods.
/// </para>
/// </remarks>
public sealed class AssistSessionContext
{
    /// <summary>Shell kind reported by the host ("pwsh", "bash", ...), or null when unknown.</summary>
    public string? ShellKind { get; private set; }

    /// <summary>Working directory as last reported by the host or by an OSC 133 event.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>Connection profile id, recorded on captured history entries.</summary>
    public string? ProfileId { get; private set; }

    /// <summary>Terminal session id, recorded on captured history entries.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Remote host id for SSH sessions, recorded on captured history entries.</summary>
    public string? HostId { get; private set; }

    /// <summary>Whether this session runs over SSH.</summary>
    public bool IsRemote { get; private set; }

    /// <summary>
    /// Whether the terminal is showing an alternate screen (a full-screen TUI). Every assist surface
    /// stays down while this is true.
    /// </summary>
    public bool IsAltScreenActive { get; private set; }

    /// <summary>Whether shell integration is configured for this session.</summary>
    public bool IsShellIntegrationEnabled { get; private set; }

    /// <summary>
    /// Whether any OSC 133 marker has been seen on this session. Reserved for the Phase 2 work that
    /// lifts the SSH restrictions once a remote shell proves it is instrumented; nothing reads it
    /// yet, and it is kept only so that the observation is not lost.
    /// </summary>
    public bool HasObservedShellIntegrationMarker { get; private set; }

    /// <summary>
    /// Whether a <c>CommandAccepted</c> (OSC 133;C) marker has been seen. This is the one that
    /// matters for capture: until the shell proves it reports command text, the heuristic Enter-time
    /// capture has to stand in for it.
    /// </summary>
    public bool HasObservedStructuredCommandCaptureMarker { get; private set; }

    /// <summary>
    /// True when structured capture can be trusted to record this session's commands, which is what
    /// tells the heuristic Enter-time path to stand down.
    /// </summary>
    public bool IsStructuredCaptureActive =>
        IsShellIntegrationEnabled && HasObservedStructuredCommandCaptureMarker;

    /// <summary>Replaces the host-reported session facts.</summary>
    /// <remarks>
    /// Observed markers survive only while shell integration stays configured: a session that
    /// switched integration off has to prove itself again.
    /// </remarks>
    public void UpdateSession(
        string? shellKind,
        string? workingDirectory,
        string? profileId,
        string? sessionId,
        string? hostId,
        bool isRemote,
        bool isShellIntegrated)
    {
        ShellKind = shellKind;
        WorkingDirectory = workingDirectory;
        ProfileId = profileId;
        SessionId = sessionId;
        HostId = hostId;
        IsRemote = isRemote;
        IsShellIntegrationEnabled = isShellIntegrated;
        HasObservedShellIntegrationMarker = HasObservedShellIntegrationMarker && isShellIntegrated;
        HasObservedStructuredCommandCaptureMarker = HasObservedStructuredCommandCaptureMarker && isShellIntegrated;
    }

    /// <summary>Turns shell integration on or off, forgetting observed markers when it goes off.</summary>
    public void SetShellIntegrationEnabled(bool isEnabled)
    {
        IsShellIntegrationEnabled = isEnabled;
        if (!isEnabled)
        {
            HasObservedShellIntegrationMarker = false;
            HasObservedStructuredCommandCaptureMarker = false;
        }
    }

    /// <summary>Records an alt-screen switch.</summary>
    public void SetAltScreenActive(bool isActive) => IsAltScreenActive = isActive;

    /// <summary>Records a working-directory change reported by a shell-integration event.</summary>
    public void SetWorkingDirectory(string workingDirectory) => WorkingDirectory = workingDirectory;

    /// <summary>Records that the shell emitted some OSC 133 marker.</summary>
    public void ObserveShellIntegrationMarker() => HasObservedShellIntegrationMarker = true;

    /// <summary>Records that the shell emitted a command-accepted marker carrying command text.</summary>
    public void ObserveStructuredCommandCaptureMarker() => HasObservedStructuredCommandCaptureMarker = true;

    /// <summary>Builds the immutable snapshot the help/fix providers are queried with.</summary>
    public CommandAssistContextSnapshot CreateSnapshot(string queryText, string? selectedText)
    {
        string recognizedSource = string.IsNullOrWhiteSpace(queryText) ? selectedText ?? string.Empty : queryText;

        return new CommandAssistContextSnapshot(
            QueryText: queryText,
            RecognizedCommand: RecognizedCommandParser.ParsePrimaryCommand(recognizedSource),
            ShellKind: ShellKind,
            WorkingDirectory: WorkingDirectory,
            ProfileId: ProfileId,
            SessionId: SessionId,
            HostId: HostId,
            IsRemote: IsRemote,
            SelectedText: selectedText);
    }
}
