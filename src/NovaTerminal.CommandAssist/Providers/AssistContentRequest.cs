using System;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// Everything a content provider is allowed to see about the user's session. Constructed in exactly
/// one place - <see cref="AssistContentRequestFactory"/> - and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every free-text member is a <see cref="RedactedText"/>.</strong> There is no exemption and
/// no "this one is safe" field: command text, the output tail, a user selection, the working
/// directory. Running the filter over a path is a no-op in practice, and the value of having no
/// exceptions is that the rule needs no judgement to apply and no argument to review. What is left as
/// a plain scalar is not free text at all - a shell kind this app itself determined, an integer exit
/// code, a boolean, and an opaque session id.
/// </para>
/// <para>
/// <strong>The constructor is <see langword="internal"/>.</strong> Providers consume requests; only
/// the assist assembly may make one. That closes the other half of the loop that
/// <see cref="RedactedText"/> opens: a provider in another assembly can neither mint redacted text nor
/// fabricate a request around text it obtained some other way, so the only requests in existence are
/// the ones the factory built.
/// </para>
/// <para>
/// <strong>No history, no scrollback, no environment.</strong> The request is the failing command (or
/// the command being asked about) plus the bounded tail of its output. It deliberately does not carry
/// the history corpus, the full scrollback, environment variables or the profile - none of which a
/// content question needs, and all of which would be a much larger thing to hand to a future network
/// provider. Widening this record is the decision point where that changes, which is why it is one
/// record in one file.
/// </para>
/// </remarks>
public sealed record AssistContentRequest
{
    internal AssistContentRequest(
        AssistCapabilities capability,
        RedactedText commandText,
        RedactedText? commandToken,
        RedactedText? outputTail,
        RedactedText? selectedText,
        RedactedText? workingDirectory,
        string? shellKind,
        int? exitCode,
        bool isRemote,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(commandText);

        Capability = capability;
        CommandText = commandText;
        CommandToken = commandToken;
        OutputTail = outputTail;
        SelectedText = selectedText;
        WorkingDirectory = workingDirectory;
        ShellKind = shellKind;
        ExitCode = exitCode;
        IsRemote = isRemote;
        SessionId = sessionId;
    }

    /// <summary>
    /// The single question being asked. Exactly one flag; the factory rejects a composite.
    /// </summary>
    public AssistCapabilities Capability { get; }

    /// <summary>
    /// The command the question is about - what failed, or what is on the line Help was asked from.
    /// </summary>
    public RedactedText CommandText { get; }

    /// <summary>
    /// The command token as this app resolved it (<c>git rebase</c> out of <c>git rebase -i HEAD~3</c>),
    /// or <see langword="null"/> when nothing recognisable was on the line.
    /// </summary>
    public RedactedText? CommandToken { get; }

    /// <summary>
    /// The bounded tail of what the command printed - last 40 logical lines / 8 KB - or
    /// <see langword="null"/> when the session had no marks to bound the output region.
    /// </summary>
    public RedactedText? OutputTail { get; }

    /// <summary>A selection the user explicitly asked about, for the Explain path.</summary>
    public RedactedText? SelectedText { get; }

    /// <summary>Where the command ran, when <c>OSC 7</c> reported it.</summary>
    public RedactedText? WorkingDirectory { get; }

    /// <summary>
    /// <c>pwsh</c>, <c>powershell</c>, <c>cmd</c>, <c>bash</c>, <c>zsh</c>, <c>fish</c> or
    /// <see langword="null"/>. Derived from the launch command by this app, never from grid text.
    /// </summary>
    public string? ShellKind { get; }

    /// <summary>The shell-reported exit code, when <c>OSC 133;D</c> carried one.</summary>
    public int? ExitCode { get; }

    /// <summary>Whether the pane is an SSH session.</summary>
    public bool IsRemote { get; }

    /// <summary>
    /// The pane's session id. An opaque local correlator - it is not a user identifier and does not
    /// survive a restart.
    /// </summary>
    public string? SessionId { get; }
}
