using System;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.Core;

namespace NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

public sealed class PowerShellShellIntegrationProvider : IShellIntegrationProvider
{
    public bool CanIntegrate(string? shellKind, TerminalProfile? profile)
    {
        if (string.Equals(shellKind, "pwsh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = profile?.Command ?? string.Empty;
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

        string bootstrapScriptPath = PowerShellBootstrapBuilder.WriteScript(AppPaths.CommandAssistDirectory);
        string mergedArguments = BuildPowerShellArguments(shellArguments, bootstrapScriptPath);
        return new ShellIntegrationLaunchPlan(
            IsIntegrated: true,
            ShellCommand: shellCommand,
            ShellArguments: mergedArguments,
            BootstrapScriptPath: bootstrapScriptPath);
    }

    private static string BuildPowerShellArguments(string? shellArguments, string bootstrapScriptPath)
    {
        string original = shellArguments?.Trim() ?? string.Empty;
        string quotedBootstrapPath = $"\"{bootstrapScriptPath}\"";

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

        if (!original.Contains("-File", StringComparison.OrdinalIgnoreCase))
        {
            original = string.IsNullOrWhiteSpace(original)
                ? $"-File {quotedBootstrapPath}"
                : $"{original} -File {quotedBootstrapPath}";
        }

        return original.Trim();
    }

    private static bool ContainsUserScriptFile(string? shellArguments)
    {
        return !string.IsNullOrWhiteSpace(shellArguments) &&
               shellArguments.Contains("-File", StringComparison.OrdinalIgnoreCase);
    }
}
