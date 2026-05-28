using System;
using System.Collections.Generic;
using System.IO;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.CommandAssist.ShellIntegration.Zsh;

public sealed class ZshShellIntegrationProvider : IShellIntegrationProvider
{
    public bool CanIntegrate(string? shellKind, TerminalProfile? profile)
    {
        if (string.Equals(shellKind, "zsh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = profile?.Command ?? string.Empty;
        return command.Contains("zsh", StringComparison.OrdinalIgnoreCase);
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

        string bootstrapScriptPath = ZshBootstrapBuilder.WriteScript(AppPaths.CommandAssistDirectory);
        string? zdotdir = Path.GetDirectoryName(bootstrapScriptPath);
        if (string.IsNullOrEmpty(zdotdir))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        var envOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ZDOTDIR"] = zdotdir
        };

        return new ShellIntegrationLaunchPlan(
            IsIntegrated: true,
            ShellCommand: shellCommand,
            ShellArguments: shellArguments,
            BootstrapScriptPath: bootstrapScriptPath,
            EnvironmentOverrides: envOverrides);
    }

    private static bool HasIncompatibleStartupMode(string? shellArguments)
    {
        if (string.IsNullOrWhiteSpace(shellArguments))
        {
            return false;
        }

        foreach (string token in shellArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // -c runs zsh in non-interactive mode; --no-rcs / -f skip startup
            // files, defeating the bootstrap. Either is incompatible with
            // automatic shell integration injection.
            if (token == "-c" || token == "--no-rcs" || token == "-f")
            {
                return true;
            }
        }

        return false;
    }
}
