using System;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

public sealed class PowerShellShellIntegrationProvider : IShellIntegrationProvider
{
    private readonly Func<string> _bootstrapDirectory;

    /// <param name="bootstrapDirectory">
    /// Resolves the directory the generated bootstrap script is written to. Supplied by the App
    /// (<c>() =&gt; AppPaths.CommandAssistDirectory</c>). Deliberately a factory, not a string: the
    /// path is resolved per <see cref="CreateLaunchPlan"/> call so it tracks app-state changes and
    /// so a resolution failure surfaces inside the caller's try/catch rather than at construction
    /// time.
    /// </param>
    public PowerShellShellIntegrationProvider(Func<string> bootstrapDirectory)
    {
        _bootstrapDirectory = bootstrapDirectory;
    }

    public bool CanIntegrate(string? shellKind, string? shellCommand)
    {
        if (string.Equals(shellKind, "pwsh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = shellCommand ?? string.Empty;
        return command.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
               command.Contains("powershell", StringComparison.OrdinalIgnoreCase);
    }

    public ShellIntegrationLaunchPlan CreateLaunchPlan(string shellCommand, string? shellArguments, string? workingDirectory)
    {
        if (ContainsUserScriptFile(shellArguments))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        // The script is still written to disk, but the on-disk copy is now purely diagnostic
        // (and what the remote-host installer reuses) — what the shell actually executes is the
        // encoded copy on the command line. BootstrapScriptPath stays on the plan for both.
        string bootstrapScriptPath = PowerShellBootstrapBuilder.WriteScript(_bootstrapDirectory());
        string mergedArguments = BuildPowerShellArguments(
            shellArguments,
            PowerShellBootstrapBuilder.BuildScript());
        return new ShellIntegrationLaunchPlan(
            IsIntegrated: true,
            ShellCommand: shellCommand,
            ShellArguments: mergedArguments,
            BootstrapScriptPath: bootstrapScriptPath);
    }

    /// <summary>
    /// Builds <c>-NoLogo -NoExit -EncodedCommand &lt;base64&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The bootstrap is passed as an encoded command rather than <c>-File</c> because
    /// <c>-File</c> is gated by PowerShell's execution policy. The stock Windows client
    /// default is <c>Restricted</c>, which blocks every script file, so every PowerShell tab
    /// opened with a red <c>UnauthorizedAccess</c> error and Command Assist silently did not
    /// work. Reported from a real first-run install.
    ///
    /// <c>-EncodedCommand</c> was chosen over <c>-ExecutionPolicy Bypass</c> for two reasons:
    /// <c>-ExecutionPolicy</c> on the command line is IGNORED when policy is set through Group
    /// Policy, so it would not fix managed machines at all; and it relaxes the policy for the
    /// whole session, meaning scripts the USER later runs in that tab would also bypass — a
    /// side effect a terminal has no business imposing. Nothing is loaded from disk here, so
    /// no policy applies, and the user's own policy is left exactly as they set it.
    ///
    /// Size is not a concern: the emitted script is ~2.7 KB, so ~7.4 KB once base64'd from
    /// UTF-16LE, against a command-line limit of 32767.
    /// </remarks>
    private static string BuildPowerShellArguments(string? shellArguments, string bootstrapScript)
    {
        string original = shellArguments?.Trim() ?? string.Empty;

        if (!original.Contains("-NoLogo", StringComparison.OrdinalIgnoreCase))
        {
            original = string.IsNullOrWhiteSpace(original)
                ? "-NoLogo"
                : $"-NoLogo {original}";
        }

        if (!original.Contains("-NoExit", StringComparison.OrdinalIgnoreCase))
        {
            original = string.IsNullOrWhiteSpace(original)
                ? "-NoExit"
                : $"{original} -NoExit";
        }

        // -EncodedCommand goes LAST and unquoted. PowerShell treats every token after it as
        // part of the command, so an argument placed afterwards is silently absorbed rather
        // than applied. Base64 is alphanumeric plus '+/=' and never needs quoting.
        string encoded = EncodeCommand(bootstrapScript);
        original = string.IsNullOrWhiteSpace(original)
            ? $"-EncodedCommand {encoded}"
            : $"{original} -EncodedCommand {encoded}";

        return original.Trim();
    }

    /// <summary>Base64 of the UTF-16LE bytes — the encoding PowerShell's -EncodedCommand expects.</summary>
    private static string EncodeCommand(string script)
        => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// True when the user's own arguments already carry a script or command, in which case we
    /// stay out of the way entirely.
    /// </summary>
    /// <remarks>
    /// <c>-Command</c> and <c>-EncodedCommand</c> have to be caught alongside <c>-File</c>:
    /// appending ours after the user's would be swallowed into theirs, and prepending would
    /// swallow theirs into ours. Neither is recoverable, so integration is declined.
    /// </remarks>
    private static bool ContainsUserScriptFile(string? shellArguments)
    {
        if (string.IsNullOrWhiteSpace(shellArguments))
        {
            return false;
        }

        return shellArguments.Contains("-File", StringComparison.OrdinalIgnoreCase) ||
               shellArguments.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase) ||
               shellArguments.Contains("-Command", StringComparison.OrdinalIgnoreCase);
    }
}
