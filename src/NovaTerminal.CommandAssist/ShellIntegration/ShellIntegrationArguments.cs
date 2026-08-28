using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration;

/// <summary>
/// Removes arguments this app previously injected from a stored command line.
/// </summary>
/// <remarks>
/// A pane used to persist the arguments it was <em>launched</em> with, which included the
/// shell-integration bootstrap. On the next launch the provider saw its own <c>-File</c> (or, after
/// the execution-policy fix, its own <c>-EncodedCommand</c>) in the incoming arguments, took the
/// "the user supplied a script" bail-out, and passed the stale command line through unchanged —
/// so integration silently stopped and the old bootstrap kept being launched. Self-perpetuating,
/// because every launch re-saved it.
///
/// The pane no longer stores the merged command line, which stops this happening again. This is
/// for the sessions already on disk, which that fix cannot reach retroactively.
///
/// Deliberately narrow on both axes:
/// <list type="bullet">
/// <item>Only arguments identifiable as ours are dropped. A user's own <c>-File</c> or
/// <c>-EncodedCommand</c> is the exact case the bail-out exists to protect, and stripping it would
/// launch a shell they did not ask for.</item>
/// <item>When nothing is dropped the input is returned <em>verbatim</em>. Anything else rewrites
/// command lines this class has no business touching.</item>
/// </list>
/// </remarks>
public static class ShellIntegrationArguments
{
    private const string BootstrapFileName = "command-assist-bootstrap.ps1";

    /// <summary>The sentinel the generated bootstrap uses to recognise its own prompt wrapper.</summary>
    private const string BootstrapSentinel = "__nova_prompt_wrapper";

    /// <param name="bootstrapDirectory">
    /// The directory this app writes its generated bootstrap into. A <c>-File</c> is only ours if
    /// it resolves to a file in here — the file NAME alone is not proof of ownership, and claiming
    /// a user's identically-named script would silently drop it from their command line.
    /// Null or unresolvable means nothing can be proven ours, so nothing is dropped.
    /// </param>
    public static string StripInjected(string? arguments, string? bootstrapDirectory)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return string.Empty;
        }

        List<(int Start, int Length)>? cuts = null;

        foreach ((string token, int start, int length) in Tokenize(arguments))
        {
            bool isFile = IsFlag(token, "-File");
            bool isEncoded = IsFlag(token, "-EncodedCommand");
            if (!isFile && !isEncoded)
            {
                continue;
            }

            if (!TryNextToken(arguments, start + length, out string value, out int valueEnd))
            {
                continue;
            }

            bool ours = isFile
                ? IsOurBootstrapPath(value, bootstrapDirectory)
                : IsOurEncodedBootstrap(value);

            if (ours)
            {
                // Swallow one adjacent delimiter with the pair, so removing it cannot leave a
                // double space behind. That is what makes a post-hoc normalising pass
                // unnecessary - and the previous global Replace("  ", " ") was re-spacing the
                // user's own quoted values as collateral. (Greptile P1 round 2 on #368.)
                int cutStart = start;
                int cutEnd = valueEnd;

                if (cutStart > 0 && arguments[cutStart - 1] == ' ')
                {
                    cutStart--;
                }
                else if (cutEnd < arguments.Length && arguments[cutEnd] == ' ')
                {
                    cutEnd++;
                }

                (cuts ??= new List<(int, int)>()).Add((cutStart, cutEnd - cutStart));
            }
        }

        // Nothing of ours in here, so this is the user's command line exactly as they wrote it.
        // Rebuilding it from tokens would collapse repeated spaces inside quoted values and
        // change what the shell runs. (Greptile P1 on #368.)
        if (cuts == null)
        {
            return arguments;
        }

        var kept = new StringBuilder(arguments.Length);
        int cursor = 0;
        foreach ((int start, int length) in cuts)
        {
            kept.Append(arguments, cursor, start - cursor);
            cursor = start + length;
        }

        kept.Append(arguments, cursor, arguments.Length - cursor);

        // No normalising pass: each cut already took its own delimiter, so what remains is
        // the user's spacing exactly as they wrote it.
        return kept.ToString();
    }

    /// <summary>Yields each whitespace-delimited token with its span in the original string.</summary>
    private static IEnumerable<(string Token, int Start, int Length)> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] == ' ') i++;
            if (i >= s.Length) yield break;

            int start = i;
            while (i < s.Length && s[i] != ' ') i++;
            yield return (s[start..i], start, i - start);
        }
    }

    private static bool TryNextToken(string s, int from, out string token, out int end)
    {
        token = string.Empty;
        end = from;

        int i = from;
        while (i < s.Length && s[i] == ' ') i++;
        if (i >= s.Length) return false;

        int start = i;
        while (i < s.Length && s[i] != ' ') i++;

        token = s[start..i];
        end = i;
        return true;
    }

    private static bool IsFlag(string token, string flag)
        => string.Equals(token, flag, StringComparison.OrdinalIgnoreCase);

    private static bool IsOurBootstrapPath(string token, string? bootstrapDirectory)
    {
        if (string.IsNullOrWhiteSpace(bootstrapDirectory))
        {
            return false;
        }

        // Compared as a whole path, not directory-plus-name. A stored path can be in 8.3 short
        // form - the PowerShell provider converts the bootstrap path to its short name when the
        // long one contains a space (a username with a space is enough) - and in that form BOTH
        // halves are unrecognisable: the directory is mangled and the file is COMMAN~1.PS1, so a
        // name pre-filter rejects our own file before any directory check runs. Matching the
        // short form of the full path is what actually identifies it.
        // (Greptile P1 round 2 on #368.)
        string path = token.Trim('"');

        try
        {
            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string ourLong = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(bootstrapDirectory, BootstrapFileName)));

            if (SamePath(candidate, ourLong))
            {
                return true;
            }

            // Only reachable while the file still exists; GetShortPathNameW resolves against the
            // filesystem. A stale short path whose file is already gone stays put, which is the
            // safe direction - it launches as the user's own rather than being silently dropped.
            string? ourShort = TryGetShortPath(ourLong);
            return ourShort != null && SamePath(candidate, Path.TrimEndingDirectorySeparator(ourShort));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool SamePath(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The 8.3 short form of an existing directory, or null when unavailable.</summary>
    private static string? TryGetShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(512);
            uint written = NativeMethods.GetShortPathNameW(path, buffer, (uint)buffer.Capacity);

            // 0 means the API failed - most often the path no longer exists, or the volume has
            // 8.3 name generation disabled. Either way there is no short form to compare against.
            return written == 0 || written > buffer.Capacity ? null : buffer.ToString();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern uint GetShortPathNameW(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpszLongPath,
            StringBuilder lpszShortPath,
            uint cchBuffer);
    }

    private static bool IsOurEncodedBootstrap(string token)
    {
        // A user's own encoded command must survive, so this decodes and looks for the
        // sentinel rather than assuming any -EncodedCommand is ours. Malformed base64 is
        // a user's business, not a reason to fail a pane launch.
        try
        {
            string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(token));
            return decoded.Contains(BootstrapSentinel, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
