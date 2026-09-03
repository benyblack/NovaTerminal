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

        // The UI accent is derived from the theme's ANSI blue rather than copied: ANSI blues are
        // tuned for terminal text (the Default theme's is RGB(0,0,238)) and read as eye-searing
        // when lifted wholesale into buttons, toggles, and sliders. The terminal palette itself
        // is untouched - only these chrome brushes are softened.
        var accent = SoftenAccent(theme.Blue.ToAvaloniaColor());
        UpdateBrush(resources, "NtBlue", accent);
        UpdateBrush(resources, "NtBlueDim", WithAlpha(accent, 0x24));
        UpdateBrush(resources, "NtBlueFaint", WithAlpha(accent, 0x15));
        UpdateBrush(resources, "NtBlueBorder", WithAlpha(accent, 0x4D));
        UpdateBrush(resources, "NtAccentHover", ShiftLightness(accent, 0.08));
        UpdateBrush(resources, "NtAccentPressed", ShiftLightness(accent, -0.06));
        UpdateBrush(resources, "NtAccentFg", IsDark(accent) ? Colors.White : Color.Parse("#0a1622"));
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

    /// <summary>
    /// Pulls a theme's ANSI blue into a range that works as UI chrome: saturation capped at 0.68
    /// and lightness held between 0.45 and 0.62. Accents already inside those caps pass through
    /// unchanged; extreme ones like the Default theme's RGB(0,0,238) land on a comfortable
    /// indigo-blue. Hue is never shifted, so the accent keeps the theme's identity.
    /// </summary>
    public static Color SoftenAccent(Color color)
    {
        var (h, s, l) = ToHsl(color);
        s = Math.Min(s, 0.68);
        l = Math.Clamp(l, 0.45, 0.62);
        return FromHsl(color.A, h, s, l);
    }

    private static Color ShiftLightness(Color color, double delta)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(color.A, h, s, Math.Clamp(l + delta, 0.0, 1.0));
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        // Achromatic and channel-dominance comparisons run on the source bytes: they are exact
        // integer checks, immune to the float-equality warning the scaled doubles would raise.
        byte maxByte = Math.Max(c.R, Math.Max(c.G, c.B));
        byte minByte = Math.Min(c.R, Math.Min(c.G, c.B));

        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = maxByte / 255.0, min = minByte / 255.0;
        double l = (max + min) / 2.0;

        if (maxByte == minByte)
        {
            // Achromatic: saturation is zero by definition.
            return (0.0, 0.0, l);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (maxByte == c.R)
        {
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        }
        else if (maxByte == c.G)
        {
            h = (b - r) / d + 2.0;
        }
        else
        {
            h = (r - g) / d + 4.0;
        }

        return (h * 60.0, s, l);
    }

    private static Color FromHsl(byte alpha, double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hPrime = h / 60.0;
        double x = c * (1.0 - Math.Abs(hPrime % 2.0 - 1.0));
        double r = 0, g = 0, b = 0;
        if (hPrime < 1) { r = c; g = x; }
        else if (hPrime < 2) { r = x; g = c; }
        else if (hPrime < 3) { g = c; b = x; }
        else if (hPrime < 4) { g = x; b = c; }
        else if (hPrime < 5) { r = x; b = c; }
        else { r = c; b = x; }

        double m = l - c / 2.0;
        return Color.FromArgb(
            alpha,
            (byte)Math.Round(Math.Clamp(r + m, 0.0, 1.0) * 255.0),
            (byte)Math.Round(Math.Clamp(g + m, 0.0, 1.0) * 255.0),
            (byte)Math.Round(Math.Clamp(b + m, 0.0, 1.0) * 255.0));
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
