using System.Globalization;
using System.Linq;
using System.Text;

namespace NovaTerminal.Core;

public static class UnicodeWidth
{
    public static int GetGraphemeWidth(string textElement)
    {
        if (string.IsNullOrEmpty(textElement))
        {
            return 0;
        }

        if (textElement.Length == 1)
        {
            return Rune.TryCreate(textElement[0], out var rune) ? GetRuneWidth(rune) : 1;
        }

        int totalBaseWidth = 0;
        int regionalIndicatorCount = 0;
        int nonRegionalBaseCount = 0;
        bool hasEmojiModifier = false;
        bool hasEmojiPresentation = false;
        bool hasTextPresentation = false;
        bool hasZwj = false;
        bool hasAmbiguousSymbolBase = false;
        bool hasWideEmojiBase = false;

        foreach (var rune in textElement.EnumerateRunes())
        {
            if (IsZeroWidthJoiner(rune))
            {
                hasZwj = true;
                continue;
            }

            if (IsVariationSelector15(rune))
            {
                hasTextPresentation = true;
                continue;
            }

            if (IsVariationSelector16(rune))
            {
                hasEmojiPresentation = true;
                continue;
            }

            if (IsEmojiModifier(rune))
            {
                hasEmojiModifier = true;
                continue;
            }

            if (IsZeroWidthGraphemeComponent(rune))
            {
                continue;
            }

            if (IsAmbiguousEmojiBase(rune))
            {
                hasAmbiguousSymbolBase = true;
            }

            if (IsRegionalIndicator(rune))
            {
                regionalIndicatorCount++;
            }
            else
            {
                nonRegionalBaseCount++;
            }

            int width = GetRuneWidth(rune);
            totalBaseWidth += width;

            if (width == 2 && (IsRegionalIndicator(rune) || IsEmojiRange(rune)))
            {
                hasWideEmojiBase = true;
            }
        }

        if (regionalIndicatorCount > 0 && nonRegionalBaseCount == 0)
        {
            int pairs = regionalIndicatorCount / 2;
            int remainder = regionalIndicatorCount % 2;
            return (pairs * 2) + (remainder * 2);
        }

        if (hasZwj)
        {
            if (hasWideEmojiBase || hasAmbiguousSymbolBase)
            {
                return 2;
            }

            return totalBaseWidth == 0 ? 0 : Math.Max(1, totalBaseWidth);
        }

        if (hasEmojiModifier)
        {
            if (totalBaseWidth == 0)
            {
                return 0;
            }

            return Math.Max(2, totalBaseWidth);
        }

        if (hasAmbiguousSymbolBase)
        {
            if (hasEmojiPresentation)
            {
                return Math.Max(2, totalBaseWidth);
            }

            if (hasTextPresentation)
            {
                return totalBaseWidth == 0 ? 1 : totalBaseWidth;
            }
        }

        return totalBaseWidth;
    }

    public static int GetRuneWidth(Rune rune)
    {
        if (IsZeroWidthGraphemeComponent(rune))
        {
            return 0;
        }

        int cp = rune.Value;

        if (cp < 32 || (cp >= 0x7F && cp <= 0x9F))
        {
            return 0;
        }

        if (cp >= 0x1100 && cp <= 0x115F) return 2;
        if (cp >= 0x2329 && cp <= 0x232A) return 2;
        if (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) return 2;
        if (cp >= 0xAC00 && cp <= 0xD7A3) return 2;
        if (cp >= 0xF900 && cp <= 0xFAFF) return 2;
        if (cp >= 0xFE10 && cp <= 0xFE6F) return 2;
        if (cp >= 0xFF00 && cp <= 0xFFEF) return 2;
        if (cp >= 0x1F000 && cp <= 0x1FBFF) return 2;
        if (cp >= 0x20000 && cp <= 0x3FFFF) return 2;

        return 1;
    }

    public static bool IsZeroWidthGraphemeComponent(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category == UnicodeCategory.NonSpacingMark ||
            category == UnicodeCategory.SpacingCombiningMark ||
            category == UnicodeCategory.EnclosingMark)
        {
            return true;
        }

        int value = rune.Value;
        if (value >= 0x200B && value <= 0x200F) return true;
        if (value >= 0xFE00 && value <= 0xFE0F) return true;
        if (value >= 0xE0100 && value <= 0xE01EF) return true;
        if (value >= 0x1F3FB && value <= 0x1F3FF) return true;
        if (value >= 0xE0020 && value <= 0xE007F) return true;

        return false;
    }

    public static bool ShouldAttachToPrevious(string previousGrapheme, string incomingGrapheme, bool previousEndedWithZwj)
    {
        if (string.IsNullOrEmpty(previousGrapheme) || string.IsNullOrEmpty(incomingGrapheme))
        {
            return false;
        }

        if (previousEndedWithZwj)
        {
            return true;
        }

        if (TryGetFirstRune(incomingGrapheme, out var incomingRune) && IsZeroWidthGraphemeComponent(incomingRune))
        {
            return true;
        }

        if (IsSingleRegionalIndicator(incomingGrapheme) && IsRegionalIndicatorCluster(previousGrapheme))
        {
            return CountRegionalIndicators(previousGrapheme) % 2 == 1;
        }

        return false;
    }

    public static bool IsRegionalIndicatorCluster(string text)
        => CountRegionalIndicators(text) > 0 && text.EnumerateRunes().All(IsRegionalIndicator);

    public static bool IsSingleRegionalIndicator(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsRegionalIndicator(rune))
            {
                return false;
            }

            count++;
            if (count > 1)
            {
                return false;
            }
        }

        return count == 1;
    }

    public static int CountRegionalIndicators(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRegionalIndicator(rune))
            {
                count++;
            }
        }

        return count;
    }

    public static bool IsZeroWidthJoiner(Rune rune) => rune.Value == 0x200D;

    public static bool IsRegionalIndicator(Rune rune)
        => rune.Value >= 0x1F1E6 && rune.Value <= 0x1F1FF;

    public static bool IsVariationSelector15(Rune rune) => rune.Value == 0xFE0E;

    public static bool IsVariationSelector16(Rune rune) => rune.Value == 0xFE0F;

    public static bool IsEmojiModifier(Rune rune)
        => rune.Value >= 0x1F3FB && rune.Value <= 0x1F3FF;

    private static bool IsAmbiguousEmojiBase(Rune rune)
        => rune.Value >= 0x2600 && rune.Value <= 0x27BF;

    private static bool IsEmojiRange(Rune rune)
        => rune.Value >= 0x1F000 && rune.Value <= 0x1FBFF;

    private static bool TryGetFirstRune(string text, out Rune rune)
    {
        if (string.IsNullOrEmpty(text))
        {
            rune = default;
            return false;
        }

        foreach (var candidate in text.EnumerateRunes())
        {
            rune = candidate;
            return true;
        }

        rune = default;
        return false;
    }
}
