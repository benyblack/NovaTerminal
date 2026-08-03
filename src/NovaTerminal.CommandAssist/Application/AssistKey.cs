using System;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// UI-toolkit-agnostic identity for the keys Command Assist reasons about.
/// </summary>
/// <remarks>
/// Command Assist routing must stay usable from a non-UI assembly, so it cannot name
/// <c>Avalonia.Input.Key</c>. Only the keys the router (and its tests) actually distinguish are
/// modelled; everything else maps to <see cref="None"/> at the App boundary
/// (<c>AssistKeyMapper</c>), which the router treats as "not ours".
/// </remarks>
public enum AssistKey
{
    /// <summary>Any key Command Assist does not distinguish.</summary>
    None = 0,
    Escape,
    Up,
    Down,
    Enter,
    Tab,
    P,
}

/// <summary>
/// UI-toolkit-agnostic modifier set, mirroring the subset of <c>Avalonia.Input.KeyModifiers</c>
/// that Command Assist inspects.
/// </summary>
[Flags]
public enum AssistModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,

    /// <summary>
    /// The platform command key (Windows/Super/Cmd).
    /// </summary>
    /// <remarks>
    /// Modelled even though no assist shortcut uses it, because the router's accept rule is
    /// <c>modifiers == None</c> exactly. Dropping <c>Meta</c> at the App boundary made a
    /// <c>Win+Enter</c> look unmodified to the router while <c>TerminalPane</c>'s own
    /// <c>modifiers == KeyModifiers.None</c> check saw it for what it was: the router claimed the key,
    /// the pane declined to act on it, and the hint strip agreed with neither.
    /// </remarks>
    Meta = 8,
}
