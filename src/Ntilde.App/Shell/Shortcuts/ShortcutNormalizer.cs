using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace NovaTerminal.Shell.Shortcuts;

public static class ShortcutNormalizer
{
    public static string Normalize(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            throw new ArgumentException("Shortcut binding cannot be empty.", nameof(shortcut));
        }

        shortcut = RewriteTrailingSymbolShortcut(shortcut.Trim());

        bool hasCtrl = false;
        bool hasAlt = false;
        bool hasShift = false;
        string? key = null;

        foreach (string rawToken in shortcut.Split('+', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                throw new ArgumentException($"Shortcut '{shortcut}' contains malformed separators.", nameof(shortcut));
            }

            string token = rawToken.Trim();
            if (token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                if (hasCtrl)
                {
                    throw new ArgumentException($"Shortcut '{shortcut}' repeats the Ctrl modifier.", nameof(shortcut));
                }

                hasCtrl = true;
                continue;
            }

            if (token.Equals("alt", StringComparison.OrdinalIgnoreCase))
            {
                if (hasAlt)
                {
                    throw new ArgumentException($"Shortcut '{shortcut}' repeats the Alt modifier.", nameof(shortcut));
                }

                hasAlt = true;
                continue;
            }

            if (token.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                if (hasShift)
                {
                    throw new ArgumentException($"Shortcut '{shortcut}' repeats the Shift modifier.", nameof(shortcut));
                }

                hasShift = true;
                continue;
            }

            if (key is not null)
            {
                throw new ArgumentException($"Shortcut '{shortcut}' contains more than one key token.", nameof(shortcut));
            }

            key = NormalizeKeyToken(shortcut, token);
        }

        if (key is null)
        {
            throw new ArgumentException($"Shortcut '{shortcut}' must include a key token.", nameof(shortcut));
        }

        List<string> normalized = [];
        if (hasCtrl)
        {
            normalized.Add("Ctrl");
        }

        if (hasAlt)
        {
            normalized.Add("Alt");
        }

        if (hasShift)
        {
            normalized.Add("Shift");
        }

        normalized.Add(key);
        return string.Join("+", normalized);
    }

    private static string RewriteTrailingSymbolShortcut(string shortcut)
    {
        if (shortcut.EndsWith("++", StringComparison.Ordinal))
        {
            return shortcut[..^1] + "Plus";
        }

        if (shortcut.EndsWith("+-", StringComparison.Ordinal))
        {
            return shortcut[..^1] + "Minus";
        }

        return shortcut;
    }

    private static string NormalizeKeyToken(string shortcut, string token)
    {
        return token.ToLowerInvariant() switch
        {
            "comma" => ",",
            "oemcomma" => ",",
            "," => ",",
            "oemplus" => "OemPlus",
            "plus" => "OemPlus",
            "-" => "OemMinus",
            "oemminus" => "OemMinus",
            "minus" => "OemMinus",
            "space" => "Space",
            "tab" => "Tab",
            _ => NormalizeKnownKeyToken(shortcut, token),
        };
    }

    private static string NormalizeKnownKeyToken(string shortcut, string token)
    {
        if (token.Length == 1)
        {
            char character = token[0];
            if (char.IsLetter(character))
            {
                return char.ToUpperInvariant(character).ToString();
            }

            if (char.IsDigit(character))
            {
                return character.ToString();
            }
        }

        if (Enum.TryParse<Key>(token, ignoreCase: true, out Key parsedKey))
        {
            return parsedKey.ToString();
        }

        throw new ArgumentException($"Shortcut '{shortcut}' uses unsupported key token '{token}'.", nameof(shortcut));
    }
}
