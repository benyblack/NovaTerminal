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
        return Format(e.Key, e.KeyModifiers);
    }

    /// <summary>
    /// Renders a chord in the canonical binding-string form: the same text the shortcut editor writes
    /// into <c>settings.json</c> when the user records a key.
    /// </summary>
    /// <remarks>
    /// Exposed for the Command Assist hint strip (PR #293 review, non-blocking 5), which used to build
    /// its own label and so disagreed with the Settings list about the same rebind: <c>Ctrl+1</c> read as
    /// "Ctrl+D1" on the strip because <c>Key.D1.ToString()</c> is "D1", and <c>Ctrl+,</c> read as
    /// "Ctrl+OemComma". The tokens and their order are the parser's own, so a label produced here always
    /// round-trips through <see cref="TryParse"/>.
    /// </remarks>
    public static string Format(Key key, KeyModifiers modifiers)
    {
        List<string> tokens = [];
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            tokens.Add("Ctrl");
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            tokens.Add("Alt");
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            tokens.Add("Shift");
        }

        tokens.Add(NormalizeKey(key));
        return string.Join("+", tokens);
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

    /// <summary>
    /// Parses a binding string into the key and modifiers it names.
    /// </summary>
    /// <remarks>
    /// Exposed for the Command Assist in-surface bindings (V2 Phase 3b), which are consumed by
    /// <c>CommandAssistKeyRouter</c> inside the pane rather than matched against a
    /// <see cref="KeyEventArgs"/> here. Shares the parse - and its cache - with
    /// <see cref="Matches"/> so the two cannot disagree about what a binding string means.
    /// </remarks>
    public static bool TryParse(string shortcut, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;

        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        ParsedShortcut? parsed = ParsedShortcutCache.GetOrAdd(shortcut, ParseShortcut);
        if (parsed is not ParsedShortcut result)
        {
            return false;
        }

        key = result.Key;
        modifiers = result.Modifiers;
        return true;
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
