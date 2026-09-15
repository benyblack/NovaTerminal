using System.Collections.Generic;

namespace NovaTerminal.Shell.TitleBar;

public static class TitleBarCatalog
{
    // mdi-plus is not used for new_tab: that button renders the literal "+" glyph at FontSize 18,
    // which is what ships today. Its geometry is unused but must stay non-empty so the catalog
    // invariants hold uniformly; TitleBarViewFactory special-cases the id.
    private const string GeometryPlus =
        "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z";

    private const string GeometryTabList =
        "M4,6H20V8H4V6M4,11H20V13H4V11M4,16H20V18H4V16Z";

    private const string GeometryRecord =
        "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z";

    private const string GeometryConnections =
        "M19,15H5C3.34,15 2,16.34 2,18V20C2,21.66 3.34,23 5,23H19C20.66,23 22,21.66 22,20V18C22,16.34 20.66,15 19,15M8,20C7.45,20 7,19.55 7,19C7,18.45 7.45,18 8,18H9C9.55,18 10,18.45 10,19C10,19.55 9.55,20 9,20H8M19,9H5C3.34,9 2,10.34 2,12V14C2,15.66 3.34,17 5,17H19C20.66,17 22,15.66 22,14V12C22,10.34 20.66,9 19,9M8,14C7.45,14 7,13.55 7,13C7,12.45 7.45,12 8,12H9C9.55,12 10,12.45 10,13C10,13.55 9.55,14 9,14H8M19,3H5C3.34,3 2,4.34 2,6V8C2,9.66 3.34,11 5,11H19C20.66,11 22,9.66 22,8V6C22,4.34 20.66,3 19,3M8,8C7.45,8 7,7.55 7,7C7,6.45 7.45,6 8,6H9C9.55,6 10,6.45 10,7C10,7.55 9.55,8 9,8H8Z";

    // mdi-cog
    private const string GeometrySettings =
        "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z";

    // mdi-apps
    private const string GeometryCommandPalette =
        "M16,20H20V16H16M16,14H20V10H16M10,8H14V4H10M16,8H20V4H16M10,14H14V10H10M4,8H8V4H4M4,14H8V10H4M4,20H8V16H4M10,20H14V16H10V20Z";

    // mdi-magnify
    private const string GeometryFind =
        "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";

    // Hand-authored: two panes side by side. "Vertical" names the divider, matching the
    // split_vertical command, which calls SplitPane(Orientation.Horizontal).
    private const string GeometrySplitVertical =
        "M3,3H11V21H3V3M13,3H21V21H13V3Z";

    // Hand-authored: two panes stacked.
    private const string GeometrySplitHorizontal =
        "M3,3H21V11H3V3M3,13H21V21H3V13Z";

    // mdi-folder
    private const string GeometryRemoteFiles =
        "M20,18H4V8H20M20,6H12L10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6Z";

    // mdi-swap-vertical
    private const string GeometryTransfers =
        "M9,3L5,7H8V14H10V7H13M16,17V10H14V17H11L15,21L19,17H16Z";

    // mdi-pulse
    private const string GeometryAgentActivity =
        "M3,13H5.79L10.1,4.79L11.28,13.75L14.5,9.66L17.83,13H21V15H17L14.67,12.67L9.92,18.63L8.32,6.43L7,15H3V13Z";

    /// <summary>mdi-dots-horizontal, for the overflow button itself. Not a catalog entry.</summary>
    public const string OverflowGeometry =
        "M16,12A2,2 0 0,1 18,10A2,2 0 0,1 20,12A2,2 0 0,1 18,14A2,2 0 0,1 16,12M10,12A2,2 0 0,1 12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12M4,12A2,2 0 0,1 6,10A2,2 0 0,1 8,12A2,2 0 0,1 6,14A2,2 0 0,1 4,12Z";

    /// <summary>The id of the New Tab entry, which is locked and renders its XAML-declared flyout.</summary>
    public const string NewTabId = "new_tab";

    /// <summary>
    /// The id of the Tab List entry. Also a persisted settings key and a
    /// <see cref="Shortcuts.ShortcutCatalog"/> command id, so the string value must not change.
    /// </summary>
    public const string OpenTabListId = "open_tab_list";

    private static readonly IReadOnlyList<TitleBarCatalogEntry> Entries =
    [
        new(NewTabId, "New Tab", GeometryPlus, 16, "new_tab", TitleBarItemState.Pinned, IsLocked: true, IsToggle: false),
        new(OpenTabListId, "Tab List", GeometryTabList, 16, OpenTabListId, TitleBarItemState.Pinned, IsLocked: false, IsToggle: false),
        new("connections", "Connections", GeometryConnections, 16, "connections", TitleBarItemState.Pinned, IsLocked: false, IsToggle: false),
        new("settings", "Settings", GeometrySettings, 16, "settings", TitleBarItemState.Pinned, IsLocked: false, IsToggle: false),
        new("toggle_recording", "Record Session", GeometryRecord, 14, "toggle_recording", TitleBarItemState.Overflow, IsLocked: false, IsToggle: true),
        new("command_palette", "Command Palette", GeometryCommandPalette, 16, "command_palette", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("find", "Find in Terminal", GeometryFind, 16, "find", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("split_vertical", "Split Vertical", GeometrySplitVertical, 16, "split_vertical", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("split_horizontal", "Split Horizontal", GeometrySplitHorizontal, 16, "split_horizontal", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("sftp_remote_files", "Remote Files", GeometryRemoteFiles, 16, "", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("sftp_transfers", "Transfers", GeometryTransfers, 16, "", TitleBarItemState.Overflow, IsLocked: false, IsToggle: false),
        new("agent_activity", "Agent Activity", GeometryAgentActivity, 16, "", TitleBarItemState.Hidden, IsLocked: false, IsToggle: false),
    ];

    /// <summary>The catalog in default display order.</summary>
    public static IReadOnlyList<TitleBarCatalogEntry> GetEntries() => Entries;

    /// <summary>The most items the settings UI will let the user pin. See the plan's guardrail note.</summary>
    public const int MaxPinned = 8;
}
