namespace NovaTerminal.Core
{
    /// <summary>
    /// Interface for terminal sessions to allow sending input.
    /// Used by TerminalView to forward mouse events to the shell.
    /// </summary>
    public interface ITerminalSession : System.IDisposable
    {
        void SendInput(string input);
        void Resize(int cols, int rows);
        string ShellCommand { get; }
        event System.Action<string>? OnOutputReceived;
        event System.Action<int>? OnExit;
    }
}
