using Avalonia.Media;
using NovaTerminal.Core;

namespace NovaTerminal.Core
{
    public static class TermColorHelper
    {
        public static TermColor FromAvaloniaColor(Color c) => new TermColor(c.R, c.G, c.B, c.A);
        public static Color ToAvaloniaColor(this TermColor tc) => Color.FromArgb((byte)tc.A, (byte)tc.R, (byte)tc.G, (byte)tc.B);
    }
}
