using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace NovaTerminal.Core.Shortcuts;

public static class ShortcutMatcher
{
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

        try
        {
            return string.Equals(
                ShortcutNormalizer.Normalize(shortcut),
                NormalizeEvent(e),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
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
}
