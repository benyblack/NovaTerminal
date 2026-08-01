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
}
