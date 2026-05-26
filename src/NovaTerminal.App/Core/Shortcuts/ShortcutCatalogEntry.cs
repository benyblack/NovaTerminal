namespace NovaTerminal.Core.Shortcuts;

public sealed record ShortcutCatalogEntry(
    string CommandId,
    string Title,
    string Category,
    ShortcutScope Scope,
    string DefaultBinding);
