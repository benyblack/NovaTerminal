namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Works out the delta the terminal must be sent to turn the text already typed at the prompt into
/// the selected suggestion.
/// </summary>
/// <remarks>
/// Public for the same reason as <see cref="CommandAssistKeyRouter"/>: the App's
/// <c>TerminalPane</c> calls it when accepting a suggestion, and it is a pure static function over
/// strings, so exposing it lets this assembly avoid granting the App <c>InternalsVisibleTo</c>.
/// </remarks>
public static class CommandAssistInsertionPlanner
{
    public static bool TryCreateInsertion(string? existingQuery, string? selectedCommand, out string? textToSend)
    {
        textToSend = null;

        if (string.IsNullOrEmpty(selectedCommand))
        {
            return false;
        }

        string query = existingQuery ?? string.Empty;
        if (query.Length == 0)
        {
            textToSend = selectedCommand;
            return true;
        }

        if (!selectedCommand.StartsWith(query, System.StringComparison.Ordinal))
        {
            return false;
        }

        if (selectedCommand.Length == query.Length)
        {
            return false;
        }

        textToSend = selectedCommand[query.Length..];
        return true;
    }
}
