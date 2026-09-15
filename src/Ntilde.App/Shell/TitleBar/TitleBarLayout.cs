using System.Collections.Generic;

namespace Ntilde.Shell.TitleBar;

/// <summary>What the title bar should show right now.</summary>
public sealed record TitleBarLayout(
    IReadOnlyList<TitleBarCatalogEntry> Pinned,
    IReadOnlyList<TitleBarCatalogEntry> Overflow)
{
    /// <summary>The … button is worth rendering only when it would have contents.</summary>
    public bool ShowOverflowButton => Overflow.Count > 0;
}
