using System;
using System.Collections.Generic;
using System.IO;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;

namespace NovaTerminal.CommandAssist.ShellIntegration.Zsh;

public sealed class ZshShellIntegrationProvider : IShellIntegrationProvider
{
    private readonly Func<string> _bootstrapDirectory;

    /// <param name="bootstrapDirectory">
    /// Resolves the directory the generated bootstrap script is written to. Supplied by the App
    /// (<c>() =&gt; AppPaths.CommandAssistDirectory</c>). Deliberately a factory, not a string: the
    /// path is resolved per <see cref="CreateLaunchPlan"/> call so it tracks app-state changes and
    /// so a resolution failure surfaces inside the caller's try/catch rather than at construction
    /// time.
    /// </param>
    public ZshShellIntegrationProvider(Func<string> bootstrapDirectory)
    {
        _bootstrapDirectory = bootstrapDirectory;
    }

    public bool CanIntegrate(string? shellKind, string? shellCommand)
    {
        if (string.Equals(shellKind, "zsh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = shellCommand ?? string.Empty;
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

        string bootstrapScriptPath = ZshBootstrapBuilder.WriteScript(_bootstrapDirectory());
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
