using System;

namespace NovaTerminal.Shell
{
    internal enum TabStripOrientationKind
    {
        Horizontal,
        Vertical,
    }

    /// <summary>
    /// Pure math/parsing for the tab strip layout modes. No Avalonia types so the
    /// tests stay plain [Fact]s (same split as TitleBarLayoutResolver).
    /// </summary>
    internal static class TabStripLayout
    {
        internal const double MinSidebarWidth = 140;
        internal const double MaxSidebarWidth = 600;
        internal const double DefaultSidebarWidth = 220;

        /// <summary>Settings-string → mode. Anything unrecognized is Horizontal (a typo must not
        /// be more disruptive than the default) — same contract as TitleBarLayoutResolver.ReadState.</summary>
        internal static bool IsVertical(string? orientation)
            => !string.IsNullOrWhiteSpace(orientation)
               && !double.TryParse(orientation, out _)
               && Enum.TryParse(orientation, ignoreCase: true, out TabStripOrientationKind parsed)
               && Enum.IsDefined(parsed)
               && parsed == TabStripOrientationKind.Vertical;

        internal static double ClampSidebarWidth(double width)
            => double.IsFinite(width) && width > 0
                ? Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth)
                : DefaultSidebarWidth;

        internal static double ComputeDraggedWidth(double startWidth, double startX, double currentX)
            => ClampSidebarWidth(startWidth + (currentX - startX));
    }
}
