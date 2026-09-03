using System;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Maps a fence info string to a body handler, or to null for "leave it alone".</summary>
/// <remarks>
/// A closed switch on purpose. Nothing outside this assembly registers a handler, so a
/// registration mechanism would be machinery with no consumer.
/// </remarks>
internal static class FenceBodyResolver
{
    private static readonly MarkdownFenceBody Markdown = new();
    private static readonly DiffFenceBody Diff = new();

    internal static IFenceBody? Resolve(string? info) => NormalizeInfo(info) switch
    {
        "markdown" or "md" => Markdown,
        "diff" or "patch" => Diff,
        _ => null,
    };

    /// <summary>
    /// The first whitespace-delimited token, lowercased invariantly.
    /// </summary>
    /// <remarks>
    /// The first token rather than the whole string, so <c>markdown title="README"</c> still
    /// resolves. Splitting here rather than trusting Markdig's own Info/Arguments division keeps
    /// the match independent of how the parser chooses to divide them.
    /// </remarks>
    internal static string NormalizeInfo(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = info.AsSpan().Trim();
        int end = span.IndexOfAny(' ', '\t');
        if (end >= 0)
        {
            span = span[..end];
        }

        return span.ToString().ToLowerInvariant();
    }
}
