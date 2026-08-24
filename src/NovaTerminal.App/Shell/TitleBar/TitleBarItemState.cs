namespace NovaTerminal.Shell.TitleBar;

/// <summary>Where a title bar catalog entry appears.</summary>
public enum TitleBarItemState
{
    /// <summary>Its own icon button in the title bar.</summary>
    Pinned,

    /// <summary>Inside the overflow (…) flyout.</summary>
    Overflow,

    /// <summary>Not in the title bar at all; still reachable by shortcut and command palette.</summary>
    Hidden,
}
