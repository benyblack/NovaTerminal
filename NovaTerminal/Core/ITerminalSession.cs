namespace NovaTerminal.Core
{
    /// <summary>
    /// Interface for terminal sessions to allow sending input.
    /// Used by TerminalView to forward mouse events to the shell.
    /// </summary>
    public interface ITerminalSession
    {
        void SendInput(string input);
    }
}
