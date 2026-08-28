using System;
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
/// Deliberately narrow: only arguments identifiable as ours are dropped. A user's own
/// <c>-File</c> or <c>-EncodedCommand</c> is the exact case the bail-out exists to protect, and
/// stripping it would launch a shell they did not ask for.
/// </remarks>
public static class ShellIntegrationArguments
{
    private const string BootstrapFileName = "command-assist-bootstrap.ps1";

    /// <summary>The sentinel the generated bootstrap uses to recognise its own prompt wrapper.</summary>
    private const string BootstrapSentinel = "__nova_prompt_wrapper";

    public static string StripInjected(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return string.Empty;
        }

        string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new StringBuilder();

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (IsFlag(token, "-File") && i + 1 < tokens.Length && IsOurBootstrapPath(tokens[i + 1]))
            {
                i++; // also drop the path
                continue;
            }

            if (IsFlag(token, "-EncodedCommand") && i + 1 < tokens.Length && IsOurEncodedBootstrap(tokens[i + 1]))
            {
                i++; // also drop the payload
                continue;
            }

            if (kept.Length > 0)
            {
                kept.Append(' ');
            }

            kept.Append(token);
        }

        return kept.ToString();
    }

    private static bool IsFlag(string token, string flag)
        => string.Equals(token, flag, StringComparison.OrdinalIgnoreCase);

    private static bool IsOurBootstrapPath(string token)
        => token.EndsWith(BootstrapFileName, StringComparison.OrdinalIgnoreCase);

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
