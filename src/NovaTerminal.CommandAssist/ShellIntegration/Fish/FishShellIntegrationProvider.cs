using System;
using System.Collections.Generic;
using System.IO;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.CommandAssist.ShellIntegration.Fish;

public sealed class FishShellIntegrationProvider : IShellIntegrationProvider
{
    private readonly string _bootstrapDirectory;

    /// <param name="bootstrapDirectory">
    /// Directory the generated bootstrap script is written to. Supplied by the App
    /// (<c>AppPaths.CommandAssistDirectory</c>).
    /// </param>
    public FishShellIntegrationProvider(string bootstrapDirectory)
    {
        _bootstrapDirectory = bootstrapDirectory;
    }

    public bool CanIntegrate(string? shellKind, string? shellCommand)
    {
        if (string.Equals(shellKind, "fish", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = shellCommand ?? string.Empty;
        return command.Contains("fish", StringComparison.OrdinalIgnoreCase);
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

        string bootstrapScriptPath = FishBootstrapBuilder.WriteScript(_bootstrapDirectory);
        // XDG_CONFIG_HOME must be the parent of the "fish" directory that
        // contains config.fish, not the fish directory itself.
        string? fishDir = Path.GetDirectoryName(bootstrapScriptPath);
        string? xdgConfigHome = fishDir != null ? Path.GetDirectoryName(fishDir) : null;
        if (string.IsNullOrEmpty(xdgConfigHome))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        var envOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_CONFIG_HOME"] = xdgConfigHome
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
            // -c runs fish in non-interactive command mode; --no-config / -N
            // skip the config.fish that carries the bootstrap.
            if (token == "-c" || token == "--no-config" || token == "-N")
            {
                return true;
            }
        }

        return false;
    }
}
