using System;

namespace NovaTerminal.Core.Shortcuts;

public sealed record ShortcutBindingRecord
{
    public ShortcutBindingRecord(string commandId, ShortcutScope scope, string binding)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        if (string.IsNullOrWhiteSpace(binding))
        {
            throw new ArgumentException("Binding cannot be empty.", nameof(binding));
        }

        CommandId = commandId;
        Scope = scope;
        Binding = binding;
    }

    public string CommandId { get; }

    public ShortcutScope Scope { get; }

    public string Binding { get; }
}
