using System;

namespace NovaTerminal.Core
{
    public static class TerminalLogger
    {
        public static Action<string>? OnLog { get; set; }

        public static void Log(string message)
        {
            OnLog?.Invoke(message);
        }
    }
}
