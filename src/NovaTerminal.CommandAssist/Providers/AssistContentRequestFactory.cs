using System;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// The only place an <see cref="AssistContentRequest"/> is built, and therefore the only place raw
/// session text is turned into text a provider may see.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One construction site is the structural half of the guarantee.</strong>
/// <see cref="RedactedText"/> makes it impossible to put unfiltered text into a request; this class
/// makes it possible to audit the seam by reading one file. Both are needed: the type stops the
/// accident, the single site stops the second, subtly different code path that quietly starts
/// carrying more than the first one did. <c>AssistSeamStructureTests</c> fails the build if a second
/// <c>new AssistContentRequest(</c> appears anywhere in the assembly.
/// </para>
/// <para>
/// <strong>The filter is a constructor dependency, not a static.</strong> The controller hands it the
/// same <see cref="ISecretsFilter"/> instance the capture pipeline writes history with, so "what gets
/// redacted before it is persisted" and "what gets redacted before it crosses the seam" cannot drift
/// into two different answers.
/// </para>
/// <para>
/// Every string that goes in comes out as a <see cref="RedactedText"/>. No parameter is exempted for
/// being "structured enough" - see <see cref="AssistContentRequest"/> for why the absence of
/// exceptions is the property worth having.
/// </para>
/// </remarks>
public sealed class AssistContentRequestFactory
{
    private readonly ISecretsFilter _secretsFilter;

    public AssistContentRequestFactory(ISecretsFilter secretsFilter)
    {
        ArgumentNullException.ThrowIfNull(secretsFilter);
        _secretsFilter = secretsFilter;
    }

    /// <summary>
    /// Builds the <see cref="AssistCapabilities.SuggestFix"/> request for a command that exited
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// <paramref name="context"/>'s output tail has already been redacted once, at the VT boundary in
    /// <c>TerminalPane</c>. It is redacted again here, unconditionally - see
    /// <see cref="RedactedText"/> for why a guarantee that depends on a caller having remembered is
    /// not a guarantee.
    /// </remarks>
    public AssistContentRequest CreateFixRequest(CommandFailureContext context, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Create(
            AssistCapabilities.SuggestFix,
            commandText: context.CommandText,
            commandToken: null,
            outputTail: context.OutputTail,
            selectedText: context.SelectedText,
            workingDirectory: context.WorkingDirectory,
            shellKind: context.ShellKind,
            exitCode: context.ExitCode,
            isRemote: context.IsRemote,
            sessionId: sessionId);
    }

    /// <summary>
    /// Builds a content request for the Help path: what is on the command line, the token this app
    /// recognised in it, and any selection the user asked about.
    /// </summary>
    /// <param name="capability">
    /// <see cref="AssistCapabilities.EnrichDocs"/> today. Taken as a parameter rather than hard-coded
    /// so the same construction site serves <see cref="AssistCapabilities.Explain"/> and
    /// <see cref="AssistCapabilities.NlToCommand"/> the day something asks them, instead of a second
    /// factory method growing beside this one.
    /// </param>
    public AssistContentRequest CreateHelpRequest(
        AssistCapabilities capability,
        string? commandText,
        string? commandToken,
        string? shellKind,
        string? workingDirectory,
        string? selectedText,
        string? sessionId,
        bool isRemote = false)
    {
        return Create(
            capability,
            commandText: commandText,
            commandToken: commandToken,
            outputTail: null,
            selectedText: selectedText,
            workingDirectory: workingDirectory,
            shellKind: shellKind,
            exitCode: null,
            isRemote: isRemote,
            sessionId: sessionId);
    }

    private AssistContentRequest Create(
        AssistCapabilities capability,
        string? commandText,
        string? commandToken,
        string? outputTail,
        string? selectedText,
        string? workingDirectory,
        string? shellKind,
        int? exitCode,
        bool isRemote,
        string? sessionId)
    {
        if (!IsSingleCapability(capability))
        {
            throw new ArgumentException(
                "A content request asks exactly one question; a composite capability has no single answer.",
                nameof(capability));
        }

        return new AssistContentRequest(
            capability,
            RedactedText.Redact(_secretsFilter, commandText),
            RedactedText.RedactOptional(_secretsFilter, commandToken),
            RedactedText.RedactOptional(_secretsFilter, outputTail),
            RedactedText.RedactOptional(_secretsFilter, selectedText),
            RedactedText.RedactOptional(_secretsFilter, workingDirectory),
            shellKind,
            exitCode,
            isRemote,
            sessionId);
    }

    /// <summary>Whether exactly one bit is set.</summary>
    private static bool IsSingleCapability(AssistCapabilities capability)
        => capability != AssistCapabilities.None && (capability & (capability - 1)) == AssistCapabilities.None;
}
