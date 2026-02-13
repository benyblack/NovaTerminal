namespace NovaTerminal.Core
{
    /// <summary>
    /// Interface for terminal sessions to allow sending input.
    /// Used by TerminalView to forward mouse events to the shell.
    /// </summary>
    public interface ITerminalSession : System.IDisposable
    {
        System.Guid Id { get; }
        void SendInput(string input);
        void Resize(int cols, int rows);
        string ShellCommand { get; }
        void SetSavedPassword(string password);
        void StartRecording(string filePath);
        void AttachBuffer(TerminalBuffer buffer);
        void TakeSnapshot();
        event System.Action<string>? OnOutputReceived;
        event System.Action<int>? OnExit;
    }
}
