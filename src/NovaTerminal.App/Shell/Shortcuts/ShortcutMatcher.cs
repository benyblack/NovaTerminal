using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia.Input;

namespace NovaTerminal.Shell.Shortcuts;

public static class ShortcutMatcher
{
    private static readonly ConcurrentDictionary<string, ParsedShortcut?> ParsedShortcutCache = new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(KeyEventArgs e)
    {
        return NormalizeEvent(e);
    }

    public static bool Matches(KeyEventArgs e, string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        ParsedShortcut? parsed = ParsedShortcutCache.GetOrAdd(shortcut, ParseShortcut);
        if (parsed is not ParsedShortcut expected)
        {
            return false;
        }

        KeyModifiers modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        return e.Key == expected.Key && modifiers == expected.Modifiers;
    }

    private static ParsedShortcut? ParseShortcut(string shortcut)
    {
        try
        {
            string normalized = ShortcutNormalizer.Normalize(shortcut);
            string[] tokens = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries);
            KeyModifiers modifiers = KeyModifiers.None;
            Key key = Key.None;

            foreach (string token in tokens)
            {
                switch (token)
                {
                    case "Ctrl":
                        modifiers |= KeyModifiers.Control;
                        break;
                    case "Alt":
                        modifiers |= KeyModifiers.Alt;
                        break;
                    case "Shift":
                        modifiers |= KeyModifiers.Shift;
                        break;
                    default:
                        if (!TryParseKey(token, out key))
                        {
                            return null;
                        }

                        break;
                }
            }

            return key == Key.None ? null : new ParsedShortcut(modifiers, key);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string NormalizeEvent(KeyEventArgs e)
    {
        List<string> tokens = [];
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            tokens.Add("Ctrl");
        }

        if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            tokens.Add("Alt");
        }

        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            tokens.Add("Shift");
        }

        tokens.Add(NormalizeKey(e.Key));
        return string.Join("+", tokens);
    }

    private static string NormalizeKey(Key key)
    {
        return key switch
        {
            Key.OemComma => ",",
            Key.OemPlus => "OemPlus",
            Key.OemMinus => "OemMinus",
            Key.Space => "Space",
            Key.Tab => "Tab",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            _ => key.ToString(),
        };
    }

    private static bool TryParseKey(string token, out Key key)
    {
        switch (token)
        {
            case ",":
                key = Key.OemComma;
                return true;
            case "OemPlus":
                key = Key.OemPlus;
                return true;
            case "OemMinus":
                key = Key.OemMinus;
                return true;
            case "Space":
                key = Key.Space;
                return true;
            case "Tab":
                key = Key.Tab;
                return true;
        }

        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            key = Key.D0 + (token[0] - '0');
            return true;
        }

        return Enum.TryParse(token, out key);
    }

    private readonly record struct ParsedShortcut(KeyModifiers Modifiers, Key Key);
}
