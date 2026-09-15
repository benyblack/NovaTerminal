namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

public interface IShellIntegrationProvider
{
    /// <param name="shellKind">Normalized shell family (for example <c>bash</c>, <c>pwsh</c>).</param>
    /// <param name="shellCommand">
    /// The configured shell command/executable, used as a fallback when the kind is unknown.
    /// Previously this took the App's <c>TerminalProfile</c>; only <c>Profile.Command</c> was ever
    /// read, and naming an App type here would pin this assembly to the UI project.
    /// </param>
    bool CanIntegrate(string? shellKind, string? shellCommand);

    ShellIntegrationLaunchPlan CreateLaunchPlan(
        string shellCommand,
        string? shellArguments,
        string? workingDirectory);
}
