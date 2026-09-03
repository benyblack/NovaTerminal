using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NovaTerminal.AgentOutput;

/// <summary>Resolved brush set for one render pass.</summary>
/// <remarks>
/// Extracted from <see cref="MarkdownRenderer"/> so fence-body handlers in sibling files can
/// take it. Resolution is unchanged: prefer the app's <c>Nt*</c> theme brushes, fall back to the
/// fixed palette the pane's other hand-styled surfaces hard-code.
/// </remarks>
internal sealed class MarkdownTheme
{
    private static readonly IBrush FallbackForeground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xFA));
    private static readonly IBrush FallbackSecondary = new SolidColorBrush(Color.FromRgb(0x96, 0xA0, 0xAE));
    private static readonly IBrush FallbackCodeBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));
    private static readonly IBrush FallbackPanel = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21));
    private static readonly IBrush FallbackHairline = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x35));
    private static readonly IBrush FallbackAccent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xD8));
    private static readonly IBrush FallbackAdded = new SolidColorBrush(Color.FromRgb(0x6F, 0xBF, 0x73));
    private static readonly IBrush FallbackRemoved = new SolidColorBrush(Color.FromRgb(0xD9, 0x6C, 0x6C));
    private static readonly IBrush FallbackHunk = new SolidColorBrush(Color.FromRgb(0xD6, 0xB0, 0x5C));

    internal required IBrush Foreground { get; init; }

    internal required IBrush Secondary { get; init; }

    internal required IBrush CodeBackground { get; init; }

    internal required IBrush PanelBackground { get; init; }

    internal required IBrush Hairline { get; init; }

    internal required IBrush Accent { get; init; }

    /// <summary>Diff addition lines.</summary>
    internal required IBrush Added { get; init; }

    /// <summary>Diff removal lines.</summary>
    internal required IBrush Removed { get; init; }

    /// <summary>Diff hunk headers.</summary>
    internal required IBrush Hunk { get; init; }

    internal static MarkdownTheme Resolve(StyledElement anchor)
    {
        return new MarkdownTheme
        {
            Foreground = Find(anchor, "NtFg", FallbackForeground),
            Secondary = Find(anchor, "NtFg3", FallbackSecondary),
            CodeBackground = Find(anchor, "NtPanelAlt", FallbackCodeBackground),
            PanelBackground = Find(anchor, "NtPanel", FallbackPanel),
            Hairline = Find(anchor, "NtHairline", FallbackHairline),
            Accent = Find(anchor, "NtBlue", FallbackAccent),
            Added = Find(anchor, "NtGreen", FallbackAdded),
            Removed = Find(anchor, "NtRed", FallbackRemoved),
            Hunk = Find(anchor, "NtYellow", FallbackHunk),
        };
    }

    private static IBrush Find(StyledElement anchor, string key, IBrush fallback)
    {
        // Control themes put brushes in as object values; anything that is not a brush
        // (an unexpected override) degrades to the fixed fallback rather than crashing.
        return anchor.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : fallback;
    }
}
