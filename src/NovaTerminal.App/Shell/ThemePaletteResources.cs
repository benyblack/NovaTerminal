using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NovaTerminal.VT;

namespace NovaTerminal.Shell;

internal static class ThemePaletteResources
{
    public static void Apply(IResourceDictionary resources, TerminalTheme theme)
    {
        var background = theme.Background.ToAvaloniaColor();
        var foreground = theme.Foreground.ToAvaloniaColor();
        bool dark = IsDark(background);

        UpdateBrush(resources, "NtWindowBg", background);
        UpdateBrush(resources, "NtChromeBg", Shift(background, dark ? 10 : -10));
        UpdateBrush(resources, "NtPanel", Shift(background, dark ? 18 : -18));
        UpdateBrush(resources, "NtPanelAlt", Shift(background, dark ? 6 : -6));
        UpdateBrush(resources, "NtHairline", Shift(background, dark ? 28 : -28));
        UpdateBrush(resources, "NtHairlineStrong", Shift(background, dark ? 40 : -40));
        UpdateBrush(resources, "NtFg", foreground);
        UpdateBrush(resources, "NtFg2", WithAlpha(foreground, 0xC8));
        UpdateBrush(resources, "NtFg3", WithAlpha(foreground, 0x9A));
        UpdateBrush(resources, "NtFg4", WithAlpha(foreground, 0x6E));
        UpdateBrush(resources, "NtBlue", theme.Blue.ToAvaloniaColor());
        UpdateBrush(resources, "NtBlueDim", WithAlpha(theme.Blue.ToAvaloniaColor(), 0x24));
        UpdateBrush(resources, "NtBlueFaint", WithAlpha(theme.Blue.ToAvaloniaColor(), 0x15));
        UpdateBrush(resources, "NtBlueBorder", WithAlpha(theme.Blue.ToAvaloniaColor(), 0x4D));
        UpdateBrush(resources, "NtGreen", theme.Green.ToAvaloniaColor());
        UpdateBrush(resources, "NtYellow", theme.Yellow.ToAvaloniaColor());
        UpdateBrush(resources, "NtRed", theme.Red.ToAvaloniaColor());
        UpdateBrush(resources, "NtMagenta", theme.Magenta.ToAvaloniaColor());
    }

    private static void UpdateBrush(IResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private static bool IsDark(Color color)
    {
        double luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;
        return luminance < 0.5;
    }

    private static Color Shift(Color color, int delta)
    {
        return Color.FromArgb(
            color.A,
            Clamp(color.R + delta),
            Clamp(color.G + delta),
            Clamp(color.B + delta));
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static byte Clamp(int value) => (byte)Math.Max(0, Math.Min(255, value));
}
