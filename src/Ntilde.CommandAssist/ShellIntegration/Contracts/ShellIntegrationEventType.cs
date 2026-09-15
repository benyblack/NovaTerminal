namespace NovaTerminal.CommandAssist.ShellIntegration.Contracts;

public enum ShellIntegrationEventType
{
    WorkingDirectoryChanged,
    PromptReady,
    CommandAccepted,
    CommandStarted,
    CommandFinished
}
