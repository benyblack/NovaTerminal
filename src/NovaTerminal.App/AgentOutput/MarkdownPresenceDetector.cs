using System;

namespace NovaTerminal.AgentOutput;

/// <summary>
/// Decides whether a chunk of terminal output looks like markdown, using distinct structural
/// signals rather than a single pattern.
/// </summary>
/// <remarks>
/// <para>
/// This gates the visibility of the pane's MD toggle, so both error directions matter and are
/// weighted differently:
/// </para>
/// <para>
/// <b>False positive</b> (button appears on non-markdown): cheap - clicking renders text that
/// reads fine as plain paragraphs, and the panel closes again. Terminal output is full of
/// markdown-<i>adjacent</i> fragments - a lone <c># comment</c>, <c>-----</c> separators, dash
/// bullets in build logs - so single weak signals must never trigger it.
/// </para>
/// <para>
/// <b>False negative</b> (button hidden on real markdown): costs discoverability. Two distinct
/// strong signals, or one strong signal plus a couple of list lines, is the floor real agent
/// output clears easily and incidental terminal noise does not.
/// </para>
/// <para>
/// Detection is line-structural on purpose - no regex, no markdown parse. It runs against a
/// bounded recent-output tail on a background cadence per pane.
/// </para>
/// </remarks>
public static class MarkdownPresenceDetector
{
    public static bool LooksLikeMarkdown(string text)
    {
        bool fence = false;
        bool heading = false;
        bool table = false;
        bool taskList = false;
        int listLines = 0;

        foreach (string rawLine in (text ?? string.Empty).Split('\n'))
        {
            string line = rawLine.TrimStart();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsFence(line))
            {
                fence = true;
            }
            else if (IsHeading(line))
            {
                heading = true;
            }
            else if (IsTaskList(line))
            {
                taskList = true;
                listLines++;
            }
            else
            {
                if (IsTableDelimiter(line))
                {
                    table = true;
                }

                if (IsListLine(line))
                {
                    listLines++;
                }
            }
        }

        int strongSignals = (fence ? 1 : 0) + (heading ? 1 : 0) + (table ? 1 : 0) + (taskList ? 1 : 0);
        return strongSignals >= 2 || (strongSignals >= 1 && listLines >= 2);
    }

    private static bool IsFence(string line)
        => line.StartsWith("```", StringComparison.Ordinal);

    /// <summary>1-6 '#' characters followed by whitespace - "#tag" and "##" alone do not count.</summary>
    private static bool IsHeading(string line)
    {
        int i = 0;
        while (i < line.Length && i < 6 && line[i] == '#')
        {
            i++;
        }

        return i > 0 && i < line.Length && line[i] == ' ';
    }

    /// <summary>
    /// A GFM delimiter row: pipes and dashes/colons only, with at least one of each -
    /// what separates the header of a markdown table from its body.
    /// </summary>
    private static bool IsTableDelimiter(string line)
    {
        bool hasPipe = false;
        bool hasDash = false;

        foreach (char c in line)
        {
            if (c == '|')
            {
                hasPipe = true;
            }
            else if (c == '-')
            {
                hasDash = true;
            }
            else if (c != ':' && !char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return hasPipe && hasDash;
    }

    private static bool IsTaskList(string line)
        => line.Length >= 6
           && (line[0] == '-' || line[0] == '*' || line[0] == '+')
           && line[1] == ' '
           && line[2] == '['
           && (line[3] == ' ' || line[3] == 'x' || line[3] == 'X')
           && line[4] == ']';

    /// <summary>Bullet or ordered list items. Weak signal: logs and diffs are full of dashes.</summary>
    private static bool IsListLine(string line)
    {
        if (line.Length >= 2 && (line[0] == '-' || line[0] == '*' || line[0] == '+') && line[1] == ' ')
        {
            return true;
        }

        // "1. " / "12. " - digits then a dot, then whitespace.
        int i = 0;
        while (i < line.Length && i < 3 && char.IsAsciiDigit(line[i]))
        {
            i++;
        }

        return i > 0 && i < line.Length - 1 && line[i] == '.' && line[i + 1] == ' ';
    }
}
