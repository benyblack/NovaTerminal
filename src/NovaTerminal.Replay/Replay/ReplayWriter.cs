namespace NovaTerminal.Core.Replay
{
    /// <summary>
    /// Preferred writer API for replay v2 files.
    /// Kept as a thin alias over PtyRecorder for backward compatibility.
    /// </summary>
    public sealed class ReplayWriter : PtyRecorder
    {
        public ReplayWriter(string filePath, int cols, int rows, string shell = "")
            : base(filePath, cols, rows, shell)
        {
        }
    }
}
