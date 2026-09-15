using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;
using NovaTerminal.VT;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// The fonts that ship inside the binary, so a fresh install renders correctly
    /// with no system font installed at all.
    ///
    /// Three, with distinct jobs. <see cref="DefaultTerminalFontFamily"/> is the
    /// default terminal face. <see cref="CascadiaFontFamily"/> stays bundled after
    /// ceasing to be the default, because every settings.json written before that
    /// change names it — dropping it would silently move those users to another
    /// font. <see cref="SymbolsFontFamily"/> is a symbols-only Nerd Font carrying
    /// the icon glyphs (dev/file-type/brand) that no plain monospace face has; it
    /// is loaded as a *fallback* rather than a choice, which is what lets icons
    /// work under whichever font the user actually picks.
    ///
    /// That split follows WezTerm rather than Ghostty: Ghostty bundles a fully
    /// patched Nerd Font per style (~2.2 MB each, icons only while that font is
    /// selected), where one shared symbols font costs the same once and serves
    /// every face. See docs/plans for the comparison.
    /// </summary>
    internal static class BundledFontCatalog
    {
        /// <summary>Default terminal face. Text and powerline glyphs; no icon set.</summary>
        internal const string DefaultTerminalFontFamily = "JetBrains Mono NL";

        /// <summary>Previous default, still bundled so stored settings keep resolving.</summary>
        internal const string CascadiaFontFamily = "Cascadia Mono PL";

        /// <summary>
        /// Icon glyphs only — this face has no ASCII at all, so it must never be
        /// offered as a selectable family or used as a primary typeface. Fallback
        /// only, which <see cref="Selectable"/> enforces by keeping it out of the
        /// Avalonia mappings and the settings picker.
        /// </summary>
        internal const string SymbolsFontFamily = "Symbols Nerd Font Mono";

        internal const string DefaultTerminalFontAssetUri =
            "avares://NovaTerminal/Assets/Fonts/JetBrainsMonoNL-Regular.ttf#JetBrains Mono NL";

        private sealed record BundledFont(string Family, string FileName, bool Selectable)
        {
            internal string AssetPath => $"avares://NovaTerminal/Assets/Fonts/{FileName}";

            /// <summary>Avalonia font-family URI: asset path plus the family name it contains.</summary>
            internal string AssetUri => $"{AssetPath}#{Family}";
        }

        private static readonly BundledFont[] Fonts =
        [
            new(DefaultTerminalFontFamily, "JetBrainsMonoNL-Regular.ttf", Selectable: true),
            new(CascadiaFontFamily, "CascadiaMonoPL-Regular.otf", Selectable: true),
            new(SymbolsFontFamily, "SymbolsNerdFontMono-Regular.ttf", Selectable: false),
        ];

        // One cached SKData per family. SKData is immutable and SKTypeface.FromData
        // does not take ownership, so the bytes are loaded once and every typeface
        // is created from the same buffer.
        private static readonly ConcurrentDictionary<string, Lazy<SKData?>> DataByFamily =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Families a user may pick. Excludes the symbols font, which has no ASCII.
        /// </summary>
        internal static IReadOnlyList<string> SelectableFamilies { get; } =
            Fonts.Where(f => f.Selectable).Select(f => f.Family).ToArray();

        /// <summary>
        /// Avalonia family-name to asset mappings, so a bundled family resolves for
        /// UI text layout as well as for the terminal grid. Selectable fonts only:
        /// a symbols-only face reachable by Avalonia's own fallback could render UI
        /// text as blanks.
        /// </summary>
        internal static IReadOnlyDictionary<string, FontFamily> CreateFontFamilyMappings()
        {
            var mappings = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
            foreach (var font in Fonts)
            {
                if (!font.Selectable) continue;
                mappings[font.Family] = new FontFamily(font.AssetUri);
            }
            return mappings;
        }

        /// <summary>
        /// Loads <paramref name="family"/> from the bundle, or null when it is not a
        /// bundled family. Includes the symbols font: it is not selectable, but the
        /// glyph fallback chain resolves it by name through here.
        /// </summary>
        internal static SKTypeface? TryCreateSkTypeface(string family)
        {
            var font = Fonts.FirstOrDefault(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));
            if (font == null)
            {
                return null;
            }

            try
            {
                var data = GetFontData(font);
                return data == null ? null : SKTypeface.FromData(data);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[Font][Warn] Failed to load bundled font '{family}': {ex.Message}");
                return null;
            }
        }

        /// <summary>True when <paramref name="family"/> ships inside the binary.</summary>
        internal static bool IsBundledFamily(string family) =>
            Fonts.Any(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));

        /// <summary>Raw bytes of the default terminal face.</summary>
        internal static SKData? GetBundledFontData() => GetFontData(Fonts[0]);

        private static SKData? GetFontData(BundledFont font) =>
            DataByFamily.GetOrAdd(font.Family, _ => new Lazy<SKData?>(() => LoadFontData(font))).Value;

        private static SKData? LoadFontData(BundledFont font)
        {
            // Prefer the copy next to the executable: a published single-file/AOT
            // build still lays the Assets folder down beside the binary, and reading
            // the file directly avoids inflating the asset stream into memory.
            string outputPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", font.FileName);
            if (File.Exists(outputPath))
            {
                return SKData.Create(outputPath);
            }

            using Stream assetStream = AssetLoader.Open(new Uri(font.AssetPath, UriKind.Absolute));
            return SKData.Create(assetStream);
        }
    }
}
