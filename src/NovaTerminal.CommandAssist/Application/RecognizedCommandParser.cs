using System;

namespace NovaTerminal.CommandAssist.Application;

public static class RecognizedCommandParser
{
    public static string? ParsePrimaryCommand(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        string trimmed = commandText.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] is '"' or '\'')
        {
            char quote = trimmed[0];
            int closingQuote = trimmed.IndexOf(quote, 1);
            if (closingQuote >= 0)
            {
                return trimmed.Substring(1, closingQuote - 1);
            }
        }

        int separatorIndex = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return separatorIndex >= 0
            ? trimmed[..separatorIndex]
            : trimmed;
    }
}
