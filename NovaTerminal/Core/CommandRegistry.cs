using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Core
{
    public class TerminalCommand
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "General";
        public Action Action { get; set; } = () => { };
        public string Shortcut { get; set; } = "";

        public string FullTitle => $"{Category}: {Title}";
    }

    public static class CommandRegistry
    {
        private static List<TerminalCommand> _commands = new();

        public static void Register(string title, string category, Action action, string shortcut = "")
        {
            _commands.Add(new TerminalCommand
            {
                Title = title,
                Category = category,
                Action = action,
                Shortcut = shortcut
            });
        }

        public static List<TerminalCommand> GetCommands() => _commands;

        public static List<TerminalCommand> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return _commands;

            var q = query.ToLowerInvariant();
            return _commands
                .Where(c => c.Title.ToLowerInvariant().Contains(q) || c.Category.ToLowerInvariant().Contains(q))
                .OrderBy(c => c.Category)
                .ThenBy(c => c.Title)
                .ToList();
        }
    }
}
