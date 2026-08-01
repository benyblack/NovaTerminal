namespace NovaTerminal.CommandAssist.Application;

internal static class CommandAssistInsertionPlanner
{
    internal static bool TryCreateInsertion(string? existingQuery, string? selectedCommand, out string? textToSend)
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
