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
        private static readonly Lazy<SKData?> BundledFontData = new(LoadBundledFontData);

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
                var data = GetBundledFontData();
                return data == null ? null : SKTypeface.FromData(data);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[Font][Warn] Failed to load bundled font '{family}': {ex.Message}");
                return null;
            }
        }

        internal static SKData? GetBundledFontData() => BundledFontData.Value;

        private static SKData? LoadBundledFontData()
        {
            string outputPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "CascadiaMonoPL-Regular.otf");
            if (File.Exists(outputPath))
            {
                return SKData.Create(outputPath);
            }

            using Stream assetStream = AssetLoader.Open(new Uri(DefaultTerminalFontAssetPath, UriKind.Absolute));
            return SKData.Create(assetStream);
        }
    }
}
