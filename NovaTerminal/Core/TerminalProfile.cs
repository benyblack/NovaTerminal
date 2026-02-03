using System;

namespace NovaTerminal.Core
{
    public class TerminalProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New Profile";
        public string Command { get; set; } = "cmd.exe";
        public string Arguments { get; set; } = "";
        public string StartingDirectory { get; set; } = "";

        // Overrides (null means use global settings)
        public string? ThemeName { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }

        public override string ToString() => Name;

        public static TerminalProfile CreateDefault()
        {
            return new TerminalProfile
            {
                Name = "Command Prompt",
                Command = "cmd.exe"
            };
        }
    }
}
