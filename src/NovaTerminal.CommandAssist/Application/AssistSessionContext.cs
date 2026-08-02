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
/// <para>
/// <strong>Threading contract.</strong> Every mutator here is called on the dispatcher thread - the
/// controller marshals shell-integration events and host callbacks there before touching this
/// object - but the readers are not all on it: <see cref="SuggestionOrchestrator"/> reads
/// <see cref="IsAcceptingCommandInput"/>, <see cref="IsAltScreenActive"/>,
/// <see cref="WorkingDirectory"/>, <see cref="ShellKind"/>, <see cref="ProfileId"/> and
/// <see cref="IsRemote"/> from the worker its refresh pass runs on. There is no lock, and the
/// contract that makes that safe has two halves.
/// </para>
/// <para>
/// First, <em>every field must stay reference-sized or bool-sized</em>. Those are written
/// atomically by the CLR, so a worker either sees the old value or the new one and never a torn
/// one. A future multi-field fact - a struct carrying a mark, a tuple of directory plus generation -
/// cannot simply be added here; it needs a lock or an immutable snapshot object swapped in as a
/// single reference (which is what <c>TerminalPane</c> does with its mark gate).
/// </para>
/// <para>
/// Second, <em>a stale read is tolerated rather than prevented</em>. A pass that reads the gate
/// microseconds before <c>133;C</c> closes it produces an outcome for a command line that has since
/// been submitted - and that outcome is dropped anyway, because the transition that closed the gate
/// came with a refresh that superseded the pass through
/// <c>SuggestionOrchestrator.CancelPending</c>. The window is real and the correctness argument is
/// supersession, not synchronization; anything that starts acting on these values <em>without</em>
/// going through a cancellable pass has to re-establish it.
/// </para>
/// </remarks>
internal sealed class AssistSessionContext
{
    /// <summary>
    /// Backing field for <see cref="IsAcceptingCommandInput"/>. Explicitly <c>volatile</c> - unlike
    /// its neighbours - because it is the one flag a worker thread polls in a tight decision rather
    /// than reads once as context: <see cref="SuggestionOrchestrator.TryReadQuery"/> consults it to
    /// decide whether it is legal to touch the terminal grid at all. The others are already
    /// atomic-by-width and would only gain ordering guarantees nothing depends on.
    /// </summary>
    private volatile bool _isAcceptingCommandInput;

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
    /// Whether any OSC 133 marker has been seen on this session.
    /// </summary>
    /// <remarks>
    /// V2 Phase 2b gave this a consumer. It is the runtime half of
    /// <see cref="IsShellIntegrationLive"/>: an SSH session cannot be handed an injected bootstrap,
    /// so the only evidence that the remote shell speaks OSC 133 is that it has spoken it.
    /// </remarks>
    public bool HasObservedShellIntegrationMarker { get; private set; }

    /// <summary>
    /// Whether a <c>CommandAccepted</c> (OSC 133;C) marker <em>carrying command text</em> has been
    /// seen. This is the one that matters for capture: until the shell proves it reports command
    /// text, the heuristic Enter-time capture has to stand in for it.
    /// </summary>
    /// <remarks>
    /// The "carrying command text" qualifier is load-bearing since Phase 2b. A third-party remote
    /// snippet may emit a bare <c>133;C</c> forever - it is a legal FinalTerm mark and iTerm2's and
    /// VS Code's snippets send exactly that. Setting this flag on a textless C would stand the
    /// heuristic path down in exchange for a structured path that never produces an entry, and the
    /// session would silently capture nothing at all.
    /// </remarks>
    public bool HasObservedStructuredCommandCaptureMarker { get; private set; }

    /// <summary>
    /// Whether this session is instrumented at all: either we injected a bootstrap
    /// (<see cref="IsShellIntegrationEnabled"/>) or the shell has proved it emits OSC 133 without our
    /// help (<see cref="HasObservedShellIntegrationMarker"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second disjunct is what makes an instrumented SSH host a first-class integrated session.
    /// Injection cannot cross SSH - env-var overrides and <c>--rcfile</c> do not survive the hop - so
    /// a remote that sources the shipped snippet has <see cref="IsShellIntegrationEnabled"/> false
    /// forever while emitting a perfectly good mark stream.
    /// </para>
    /// <para>
    /// The pane feeds the observation back through <see cref="UpdateSession"/> as well, and that is
    /// not redundant: <see cref="UpdateSession"/> forgets observed markers when integration is
    /// reported off, so without the feedback an ordinary directory change would wipe the
    /// remote session's hard-won instrumented status. This disjunct covers the other direction - the
    /// first <c>C</c> can reach the capture pipeline before the pane's update has been pumped
    /// through the dispatcher, and the event itself is the better evidence.
    /// </para>
    /// </remarks>
    public bool IsShellIntegrationLive => IsShellIntegrationEnabled || HasObservedShellIntegrationMarker;

    /// <summary>
    /// True when structured capture can be trusted to record this session's commands, which is what
    /// tells the heuristic Enter-time path to stand down.
    /// </summary>
    public bool IsStructuredCaptureActive =>
        IsShellIntegrationLive && HasObservedStructuredCommandCaptureMarker;

    /// <summary>
    /// Whether the shell is currently sitting in its line editor waiting for the user: opened by
    /// <c>OSC 133;B</c> (prompt end), closed again by <c>OSC 133;C</c> (the line was submitted).
    /// This is the lifecycle gate on grid-truth query reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grid reader cannot gate itself. Between the mark and the cursor there are cells; whether
    /// those cells are a command line the user is editing or the first lines of that command's
    /// output is a fact about the shell's lifecycle, not about the buffer, and only the OSC 133
    /// stream carries it. So the gate lives here, on the consumer side, and the reader's answer is
    /// consulted only while it is open.
    /// </para>
    /// <para>
    /// It is deliberately <em>not</em> conditioned on <see cref="IsShellIntegrationEnabled"/>. That
    /// flag records whether <em>we</em> injected a bootstrap; a shell that emits <c>B</c> is
    /// instrumented whether we did it or the user did, and Phase 2's instrumented-remote story
    /// depends on believing the marks rather than the injection. What closes the gate is evidence
    /// that the window ended - <c>C</c>, <c>D</c>, an alt-screen switch - never the absence of
    /// configuration.
    /// </para>
    /// <para>
    /// <c>D</c> closes it as well as <c>C</c> because a shell can reach <c>D</c> without an
    /// intervening <c>C</c>, and leaving the gate open for a command's whole run is exactly the
    /// failure it exists to prevent. The pane drops its mark on <c>D</c> too, so that case is
    /// double-covered on purpose: two independent facts have to be wrong before output can be
    /// served as a command line.
    /// </para>
    /// </remarks>
    public bool IsAcceptingCommandInput => _isAcceptingCommandInput;

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

    /// <summary>
    /// Records an alt-screen switch. Going into the alt screen closes the command-input window:
    /// the prompt that emitted <c>B</c> is not on the screen the user is looking at any more, and
    /// leaving it open would let a refresh read the TUI's own grid as a command line.
    /// </summary>
    /// <remarks>
    /// Coming back out does not reopen it. The shell repaints its prompt when the alt screen is
    /// torn down, and that repaint re-emits <c>B</c>, so the gate reopens on evidence rather than
    /// on assumption.
    /// </remarks>
    public void SetAltScreenActive(bool isActive)
    {
        IsAltScreenActive = isActive;
        if (isActive)
        {
            _isAcceptingCommandInput = false;
        }
    }

    /// <summary><c>OSC 133;B</c>: the prompt finished printing and the line editor is the user's.</summary>
    /// <remarks>
    /// <para>
    /// Refused while the alt screen is up, so that the invariant "the gate is never open during an
    /// alt screen" holds no matter which order the two facts arrive in.
    /// <see cref="SetAltScreenActive"/> closes the gate on the way in, but a full-screen TUI is free
    /// to emit <c>133;B</c> of its own afterwards - it is a perfectly legal thing for a program that
    /// draws its own prompt to do - and that would reopen a window onto the TUI's grid. Consumers
    /// already check alt-screen separately, so this is belt and braces; without it the two flags can
    /// disagree, and a self-contradicting invariant is one refactor away from being load-bearing.
    /// </para>
    /// <para>
    /// Re-emitting <c>B</c> while the gate is already open is idempotent by construction. Prompt
    /// frameworks repaint constantly and each repaint carries the mark; the pane keeps the newest
    /// mark and this keeps the gate open, which is exactly the intent.
    /// </para>
    /// </remarks>
    public void OpenCommandInputWindow()
    {
        if (IsAltScreenActive)
        {
            return;
        }

        _isAcceptingCommandInput = true;
    }

    /// <summary><c>OSC 133;C</c> / <c>OSC 133;D</c>: the line editor is closed.</summary>
    public void CloseCommandInputWindow() => _isAcceptingCommandInput = false;

    /// <summary>Records a working-directory change reported by a shell-integration event.</summary>
    public void SetWorkingDirectory(string workingDirectory) => WorkingDirectory = workingDirectory;

    /// <summary>Records that the shell emitted some OSC 133 marker.</summary>
    public void ObserveShellIntegrationMarker() => HasObservedShellIntegrationMarker = true;

    /// <summary>Records that the shell emitted a command-accepted marker carrying command text.</summary>
    /// <remarks>
    /// Only ever called for a <c>133;C</c> that actually carried text - see the remarks on
    /// <see cref="HasObservedStructuredCommandCaptureMarker"/> for why a bare <c>C</c> must not
    /// reach here.
    /// </remarks>
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
