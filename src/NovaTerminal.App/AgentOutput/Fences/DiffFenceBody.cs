using System;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Colors a unified diff by each line's leading marker.</summary>
/// <remarks>
/// A restyle, not a transform: the text is unchanged and nothing is hidden, so there is nothing
/// for the panel's switch to recover and <see cref="IsTransform"/> is false.
/// </remarks>
internal sealed class DiffFenceBody : IFenceBody
{
    private const string MonospaceFontFamily = "Cascadia Mono PL, Consolas, Menlo, monospace";

    public bool IsTransform => false;

    public Control Build(string code, MarkdownTheme theme, FenceContext context)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily(MonospaceFontFamily),
            Foreground = theme.Foreground,
        };

        string[] lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            text.Inlines?.Add(new Run
            {
                Text = i == lines.Length - 1 ? line : line + "\n",
                Foreground = BrushFor(line, theme),
            });
        }

        return text;
    }

    /// <summary>
    /// Order is load-bearing: the three-character file headers are tested before the
    /// one-character markers, or <c>+++ b/file</c> reads as an addition.
    /// </summary>
    private static IBrush BrushFor(string line, MarkdownTheme theme)
    {
        if (IsFileHeader(line, "+++") ||
            IsFileHeader(line, "---") ||
            line.StartsWith("diff --git", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal))
        {
            return theme.Secondary;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return theme.Hunk;
        }

        if (line.StartsWith("+", StringComparison.Ordinal))
        {
            return theme.Added;
        }

        if (line.StartsWith("-", StringComparison.Ordinal))
        {
            return theme.Removed;
        }

        return theme.Foreground;
    }

    /// <summary>
    /// A unified-diff file header: the three-character marker followed by a space, or the bare
    /// marker alone. Requiring the space matters because an added line whose own text starts
    /// with "++" produces "+++content" and is an addition, not a header.
    /// </summary>
    private static bool IsFileHeader(string line, string marker)
        => line.StartsWith(marker + " ", StringComparison.Ordinal) ||
           string.Equals(line, marker, StringComparison.Ordinal);
}
