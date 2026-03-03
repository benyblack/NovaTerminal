using System;

namespace NovaTerminal.Core
{
    public enum DetectedShell
    {
        Unknown,
        Pwsh,
        Cmd,
        PosixSh
    }

    public enum ShellOverride
    {
        Auto,
        Pwsh,
        Cmd,
        Posix
    }

    public class SessionContext
    {
        public DetectedShell DetectedShell { get; set; } = DetectedShell.Unknown;
        public bool IsWslSession { get; set; } = false;
        public bool IsEchoEnabled { get; set; } = true;
        public bool IsAltScreen { get; set; } = false;
        public ShellOverride ShellOverride { get; set; } = ShellOverride.Auto;
    }
}
