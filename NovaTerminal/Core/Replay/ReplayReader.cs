namespace NovaTerminal.Core.Replay
{
    /// <summary>
    /// Preferred reader API for replay files (v2 with v1 compatibility).
    /// Kept as a thin alias over ReplayRunner for backward compatibility.
    /// </summary>
    public sealed class ReplayReader : ReplayRunner
    {
        public ReplayReader(string filePath)
            : base(filePath)
        {
        }
    }
}
