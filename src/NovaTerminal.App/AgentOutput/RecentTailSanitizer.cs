using System;

namespace NovaTerminal.AgentOutput;

/// <summary>
/// Trims prompt-like lines from both ends of a recent-output snapshot, leaving what reads as the
/// command's response.
/// </summary>
/// <remarks>
/// <para>
/// The recent-tail snapshot exists for panels opened after the fact, where no OSC 133 edges
/// bound the output - so the raw tail includes the shell prompt before the command, the echoed
/// command line itself, and the next prompt after it. Rendered as markdown those are noise, and
/// they are also the most recognizable noise there is: prompts have shapes.
/// </para>
/// <para>
/// The shapes covered are the conservative set - <c>PS D:\path&gt;</c> (PowerShell),
/// <c>D:\path&gt;</c> (cmd), and <c>user@host:path$</c>-style (POSIX) - matched only at the
/// snapshot's two ends. A conservative match is the right bias here: mid-response lines are
/// never touched, so a response that happens to contain something prompt-shaped keeps it, while
/// a snapshot that leads or trails with real prompts loses nothing but chrome. Blank lines at
/// the ends go with them.
/// </para>
/// </remarks>
public static class RecentTailSanitizer
{
    private const int MaxPromptLength = 200;

    /// <summary>
    /// Extracts the most recent command's response from a recent-tail snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A long agent conversation is rounds of <i>prompt → response</i> all over the same grid, so
    /// trimming the snapshot's two ends (see <see cref="Trim"/>) cannot describe "the response":
    /// prompts are interleaved throughout, and the raw tail is a fragment of several rounds at
    /// once. Prompt-shaped lines are therefore treated as <b>segment boundaries</b>: the tail
    /// splits into per-command segments, each beginning at a prompt (whose line - prompt and
    /// echoed command alike - is dropped), and the last segment holding any content is the
    /// response the user just read on screen.
    /// </para>
    /// <para>
    /// Known limitation, accepted on purpose: an agent that prints a prompt-shaped line
    /// mid-response splits that response. Mis-splits degrade to "a later fragment of the answer",
    /// never to content from an earlier round.
    /// </para>
    /// </remarks>
    public static string ExtractLastResponse(string text)
    {
        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        List<string> current = new(lines.Length);
        List<string[]> segments = new();
        foreach (string line in lines)
        {
            if (IsPromptLike(line))
            {
                segments.Add(current.ToArray());
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        segments.Add(current.ToArray());

        // The last segment with content wins: earlier segments are earlier rounds of the
        // conversation, and a trailing bare prompt produces an empty final segment.
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            string[] segment = segments[i];
            int start = 0;
            int end = segment.Length - 1;
            while (start <= end && segment[start].Trim().Length == 0)
            {
                start++;
            }

            while (end >= start && segment[end].Trim().Length == 0)
            {
                end--;
            }

            if (start <= end)
            {
                return string.Join('\n', segment, start, end - start + 1);
            }
        }

        return string.Empty;
    }

    internal static bool IsPromptLike(string line)
    {
        if (line.Length == 0 || line.Length > MaxPromptLength)
        {
            return false;
        }

        // PowerShell: "PS D:\projects>" - and with the echoed command riding along,
        // "PS D:\projects> aichat ..." (the chevron is in the middle, not the end).
        if (line.StartsWith("PS ", StringComparison.Ordinal) && line.IndexOf('>') > 2)
        {
            return true;
        }

        // cmd: "D:\projects>" / "D:\projects> agent.exe" - drive letter, colon, backslash,
        // and the prompt chevron somewhere after the path.
        if (line.Length >= 4 && char.IsAsciiLetter(line[0]) && line[1] == ':' && line[2] == '\\' &&
            line.IndexOf('>') >= 3)
        {
            return true;
        }

        // POSIX: "user@host:~/demo$" / "user@host:~/demo$ llm ..." - user@host shape, then the
        // path separator, then the prompt character (with or without the command after it).
        int at = line.IndexOf('@');
        if (at > 0)
        {
            int colon = line.IndexOf(':', at);
            if (colon > at && line.IndexOfAny(['$', '#'], colon) > colon)
            {
                return true;
            }
        }

        return false;
    }
}
