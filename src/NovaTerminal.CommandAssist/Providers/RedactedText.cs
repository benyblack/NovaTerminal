using System;
using NovaTerminal.CommandAssist.Domain;

namespace NovaTerminal.CommandAssist.Providers;

/// <summary>
/// Text that has been through an <see cref="ISecretsFilter"/>. The only type
/// <see cref="AssistContentRequest"/> will carry free text in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type is the redaction-before-seam guarantee, and it is a type rather than a rule
/// because rules are enforced by reviewers and types are enforced by the compiler.</strong> The
/// guarantee V2 has to make is that no unredacted terminal text can reach a content provider - and a
/// future provider is the one that may put that text on a network. Written as a convention ("call
/// <c>SecretsFilter</c> before you build the request") it survives exactly as long as nobody adds a
/// second call site in a hurry. Written as a type it cannot be broken by accident: there is no public
/// constructor, no public conversion from <see cref="string"/>, and the single factory
/// (<see cref="Redact"/>) takes an <see cref="ISecretsFilter"/> as a parameter. You cannot produce a
/// <see cref="RedactedText"/> without having a filter in your hand and running it.
/// </para>
/// <para>
/// <strong>What it does not claim.</strong> It is not proof that the text is secret-free - that is a
/// claim only a perfect filter could make, and <see cref="SecretsFilter"/> is six patterns. It is
/// proof that <em>the filter ran</em>, which is the property the seam can actually be held to and the
/// one a reviewer can check by reading one file. Improving the filter improves every request without
/// touching the seam.
/// </para>
/// <para>
/// <strong>Redaction is unconditional, even for text a caller has already redacted.</strong> The
/// failing-command output tail is filtered once at the VT boundary in <c>TerminalPane</c> (where the
/// raw grid text stops being the VT layer's) and again here. That is deliberate duplication: a
/// guarantee that holds only when every upstream caller remembered is not a guarantee. Redaction is
/// idempotent in practice - the patterns do not match <c>[REDACTED]</c> - so the second pass costs a
/// regex sweep over at most 8 KB and changes nothing.
/// </para>
/// <para>
/// <see cref="Empty"/> is the answer for absent text, so a provider never has to distinguish "no
/// output" from "output that redacted to nothing".
/// </para>
/// </remarks>
public sealed record RedactedText
{
    private RedactedText(string value, bool wasRedacted)
    {
        Value = value;
        WasRedacted = wasRedacted;
    }

    /// <summary>The empty string, trivially redacted.</summary>
    public static RedactedText Empty { get; } = new(string.Empty, false);

    /// <summary>The text as the filter left it. Never <see langword="null"/>.</summary>
    public string Value { get; }

    /// <summary>
    /// Whether the filter actually replaced something. Diagnostics and tests; a provider has no use
    /// for it beyond telling "the user typed no secrets" from "we removed them".
    /// </summary>
    public bool WasRedacted { get; }

    /// <summary>Whether there is any text at all.</summary>
    public bool IsEmpty => Value.Length == 0;

    /// <summary>
    /// Runs <paramref name="filter"/> over <paramref name="rawText"/> and wraps the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Internal on purpose.</strong> Only this assembly may mint redacted text. A provider -
    /// including a future out-of-process one living in its own assembly - receives requests and never
    /// constructs them, so it has no way to smuggle raw text into the shape the seam trusts. Tests
    /// reach it through <c>InternalsVisibleTo</c>.
    /// </para>
    /// <para>
    /// Callers inside the assembly should not use this directly either: the one caller is
    /// <see cref="AssistContentRequestFactory"/>, and <c>AssistSeamStructureTests</c> fails if a
    /// second construction site for <see cref="AssistContentRequest"/> appears.
    /// </para>
    /// </remarks>
    internal static RedactedText Redact(ISecretsFilter filter, string? rawText)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (string.IsNullOrEmpty(rawText))
        {
            return Empty;
        }

        RedactionResult result = filter.Redact(rawText);
        return new RedactedText(result.RedactedText ?? string.Empty, result.WasRedacted);
    }

    /// <summary>
    /// <see cref="Redact"/>, but absent input maps to <see langword="null"/> rather than to
    /// <see cref="Empty"/> - so a request can distinguish "the shell reported no working directory"
    /// from "the working directory is the empty string".
    /// </summary>
    internal static RedactedText? RedactOptional(ISecretsFilter filter, string? rawText)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return string.IsNullOrEmpty(rawText) ? null : Redact(filter, rawText);
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
