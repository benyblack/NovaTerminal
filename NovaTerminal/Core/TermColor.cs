using System;

namespace NovaTerminal.Core
{
    /// <summary>
    /// Platform-independent color representation using RGBA components.
    /// Replaces Avalonia.Media.Color in core terminal logic to enable headless replay,
    /// WASM support, and custom GPU renderers.
    /// </summary>
    public readonly struct TermColor : IEquatable<TermColor>
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        public TermColor(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static TermColor FromRgb(byte r, byte g, byte b) => new TermColor(r, g, b, 255);
        public static TermColor FromArgb(byte a, byte r, byte g, byte b) => new TermColor(r, g, b, a);

        public uint ToUint() => (uint)((A << 24) | (R << 16) | (G << 8) | B);
        public static TermColor FromUint(uint val) => new TermColor((byte)((val >> 16) & 0xFF), (byte)((val >> 8) & 0xFF), (byte)(val & 0xFF), (byte)((val >> 24) & 0xFF));

        // Avalonia interop (for UI layer only)
        public Avalonia.Media.Color ToAvaloniaColor() => Avalonia.Media.Color.FromArgb(A, R, G, B);
        public static TermColor FromAvaloniaColor(Avalonia.Media.Color color) => new TermColor(color.R, color.G, color.B, color.A);

        // Equality
        public bool Equals(TermColor other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object? obj) => obj is TermColor other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public static bool operator ==(TermColor left, TermColor right) => left.Equals(right);
        public static bool operator !=(TermColor left, TermColor right) => !left.Equals(right);

        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

        // Common colors (matching Avalonia.Media.Colors)
        public static TermColor Black => FromRgb(0, 0, 0);
        public static TermColor White => FromRgb(255, 255, 255);
        public static TermColor LightGray => FromRgb(211, 211, 211);
        public static TermColor DarkGray => FromRgb(169, 169, 169);
        public static TermColor Red => FromRgb(255, 0, 0);
        public static TermColor Green => FromRgb(0, 128, 0);
        public static TermColor Blue => FromRgb(0, 0, 255);
        public static TermColor Yellow => FromRgb(255, 255, 0);
        public static TermColor Cyan => FromRgb(0, 255, 255);
        public static TermColor Magenta => FromRgb(255, 0, 255);
        public static TermColor Transparent => FromArgb(0, 0, 0, 0);
    }
}
