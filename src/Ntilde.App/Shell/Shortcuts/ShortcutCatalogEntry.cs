namespace Ntilde.Shell.Shortcuts;

public sealed record ShortcutCatalogEntry(
    string CommandId,
    string Title,
    string Category,
    ShortcutScope Scope,
    string DefaultBinding);
