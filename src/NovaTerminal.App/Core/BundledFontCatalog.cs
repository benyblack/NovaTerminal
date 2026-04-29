using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;

namespace NovaTerminal.Core
{
    internal static class BundledFontCatalog
    {
        internal const string DefaultTerminalFontFamily = "Cascadia Mono PL";
        internal const string DefaultTerminalFontAssetUri = "avares://NovaTerminal/Assets/Fonts/CascadiaMonoPL-Regular.otf#Cascadia Mono PL";
        private const string DefaultTerminalFontAssetPath = "avares://NovaTerminal/Assets/Fonts/CascadiaMonoPL-Regular.otf";

        internal static IReadOnlyDictionary<string, FontFamily> CreateFontFamilyMappings()
        {
            return new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultTerminalFontFamily] = new FontFamily(DefaultTerminalFontAssetUri)
            };
        }

        internal static SKTypeface? TryCreateSkTypeface(string family)
        {
            if (!string.Equals(family, DefaultTerminalFontFamily, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                string outputPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "CascadiaMonoPL-Regular.otf");
                if (File.Exists(outputPath))
                {
                    return SKTypeface.FromFile(outputPath);
                }

                using Stream assetStream = AssetLoader.Open(new Uri(DefaultTerminalFontAssetPath, UriKind.Absolute));
                using var memory = new MemoryStream();
                assetStream.CopyTo(memory);
                using var data = SKData.CreateCopy(memory.ToArray());
                return SKTypeface.FromData(data);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[Font][Warn] Failed to load bundled font '{family}': {ex.Message}");
                return null;
            }
        }
    }
}
