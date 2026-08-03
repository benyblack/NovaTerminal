namespace NovaTerminal.CommandAssist.Models;

/// <summary>
/// Everything the fix heuristics get to see about a command that exited non-zero.
/// </summary>
/// <param name="CommandText">The command as it was submitted.</param>
/// <param name="ExitCode">The shell-reported exit code, when <c>OSC 133;D</c> carried one.</param>
/// <param name="ShellKind">
/// <c>pwsh</c>, <c>powershell</c>, <c>cmd</c>, <c>bash</c>, <c>zsh</c>, <c>fish</c> or null.
/// Recognisers use it to pick between per-shell message wordings and per-shell fixes.
/// </param>
/// <param name="WorkingDirectory">Where it ran, when OSC 7 told us.</param>
/// <param name="OutputTail">
/// <para>
/// The tail of what the command printed - the last 40 logical lines / 8 KB of the grid between the
/// <c>133;C</c> and <c>133;D</c> marks, redacted by <c>ISecretsFilter</c> before it crossed into
/// this assembly. Null when the session had no marks to bound the region, when the grid could not
/// be trusted, or when nothing was captured.
/// </para>
/// <para>
/// <strong>Not stderr, and deliberately not named that.</strong> A terminal has one grid; stdout
/// and stderr are interleaved on it and nothing in the byte stream distinguishes them. Field name
/// <c>ErrorOutput</c> (V1's) promised a separate stream that has never existed at this layer.
/// Recognisers pattern-match a tail that usually ends with the error, which is a weaker and
/// truthful claim.
/// </para>
/// </param>
/// <param name="IsRemote">Whether the pane is an SSH session.</param>
/// <param name="SelectedText">A selection the user asked about, for the Explain path.</param>
public sealed record CommandFailureContext(
    string CommandText,
    int? ExitCode,
    string? ShellKind,
    string? WorkingDirectory,
    string? OutputTail,
    bool IsRemote,
    string? SelectedText);
