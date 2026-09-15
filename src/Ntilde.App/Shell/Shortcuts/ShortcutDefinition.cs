using System;

namespace NovaTerminal.Shell.Shortcuts;

public sealed record ShortcutDefinition
{
    public ShortcutDefinition(string commandId, ShortcutScope scope, string defaultBinding)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        if (string.IsNullOrWhiteSpace(defaultBinding))
        {
            throw new ArgumentException("Default binding cannot be empty.", nameof(defaultBinding));
        }

        CommandId = commandId;
        Scope = scope;
        DefaultBinding = defaultBinding;
    }

    public string CommandId { get; }

    public ShortcutScope Scope { get; }

    public string DefaultBinding { get; }
}
