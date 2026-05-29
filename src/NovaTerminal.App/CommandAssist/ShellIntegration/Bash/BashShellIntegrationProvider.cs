using System;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.Shell;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.CommandAssist.ShellIntegration.Bash;

public sealed class BashShellIntegrationProvider : IShellIntegrationProvider
{
    public bool CanIntegrate(string? shellKind, TerminalProfile? profile)
    {
        if (string.Equals(shellKind, "bash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = profile?.Command ?? string.Empty;
        return command.Contains("bash", StringComparison.OrdinalIgnoreCase);
    }

    public ShellIntegrationLaunchPlan CreateLaunchPlan(string shellCommand, string? shellArguments, string? workingDirectory)
    {
        if (HasIncompatibleStartupMode(shellArguments))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        string bootstrapScriptPath = BashBootstrapBuilder.WriteScript(AppPaths.CommandAssistDirectory);
        string mergedArguments = BuildBashArguments(shellArguments, bootstrapScriptPath);
        return new ShellIntegrationLaunchPlan(
            IsIntegrated: true,
            ShellCommand: shellCommand,
            ShellArguments: mergedArguments,
            BootstrapScriptPath: bootstrapScriptPath);
    }

    private static string BuildBashArguments(string? shellArguments, string bootstrapScriptPath)
    {
        string original = shellArguments?.Trim() ?? string.Empty;
        string quotedBootstrapPath = QuoteIfNeeded(bootstrapScriptPath);
        string rcfileSegment = $"--rcfile {quotedBootstrapPath}";

        string merged = string.IsNullOrWhiteSpace(original)
            ? rcfileSegment
            : $"{rcfileSegment} {original}";

        if (!ContainsInteractiveFlag(merged))
        {
            merged = $"{merged} -i";
        }

        return merged.Trim();
    }

    private static bool ContainsInteractiveFlag(string arguments)
    {
        // Whole-token check for `-i` or `--login`-style interactive shells.
        foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "-i" || token == "--login" || token == "-l")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasIncompatibleStartupMode(string? shellArguments)
    {
        if (string.IsNullOrWhiteSpace(shellArguments))
        {
            return false;
        }

        foreach (string token in shellArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "-c" || token == "--rcfile" || token == "--init-file")
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteIfNeeded(string path)
    {
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
