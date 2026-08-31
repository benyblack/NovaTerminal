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

    public static string Trim(string text)
    {
        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        int start = 0;
        int end = lines.Length - 1;

        while (start <= end && (lines[start].Length == 0 || IsPromptLike(lines[start])))
        {
            start++;
        }

        while (end >= start && (lines[end].Length == 0 || IsPromptLike(lines[end])))
        {
            end--;
        }

        return start > end
            ? string.Empty
            : string.Join('\n', lines, start, end - start + 1);
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
