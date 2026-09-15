using SkiaSharp;
using System;
using Xunit;

namespace NovaTerminal.Tests.Infra
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class FontGoldenFactAttribute : FactAttribute
    {
        public FontGoldenFactAttribute(params string[] requiredFonts)
        {
            if (!IsEnabled())
            {
                Skip = "ENABLE_FONT_GOLDENS is not set to 1. Skipping optional OS/font goldens.";
                return;
            }

            if (requiredFonts == null || requiredFonts.Length == 0)
            {
                return;
            }

            foreach (string family in requiredFonts)
            {
                using var matched = SKFontManager.Default.MatchFamily(family);
                if (matched != null)
                {
                    return;
                }
            }

            Skip = $"Missing required fonts for optional font golden: [{string.Join(", ", requiredFonts)}].";
        }

        private static bool IsEnabled()
        {
            string? raw = Environment.GetEnvironmentVariable("ENABLE_FONT_GOLDENS");
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
