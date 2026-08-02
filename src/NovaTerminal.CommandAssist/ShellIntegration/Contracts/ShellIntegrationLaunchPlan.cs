using System.Collections.Generic;

namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

public sealed record ShellIntegrationLaunchPlan(
    bool IsIntegrated,
    string ShellCommand,
    string? ShellArguments,
    string? BootstrapScriptPath,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null);
