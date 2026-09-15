namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// One customizable title bar action. <paramref name="ShortcutKey"/> is a
/// <see cref="Shortcuts.ShortcutCatalog"/> command id used only to display the current binding —
/// empty for actions with no binding. Ids here are their own namespace, deliberately not
/// CommandRegistry ids: two registry entries register with an empty id (so theirs falls back to
/// their title) and two of these actions have no registry entry at all.
/// </summary>
public sealed record TitleBarCatalogEntry(
    string Id,
    string Title,
    string IconGeometry,
    double IconSize,
    string ShortcutKey,
    TitleBarItemState DefaultState,
    bool IsLocked,
    bool IsToggle);
