using System.Text;
using System.Linq;

namespace NovaTerminal.Core.Replay
{
    public class BufferSnapshot
    {
        public int CursorCol { get; set; }
        public int CursorRow { get; set; }
        public bool IsAltScreen { get; set; }
        public string[] Lines { get; set; } = System.Array.Empty<string>();

        public static BufferSnapshot Capture(TerminalBuffer buffer)
        {
            var snapshot = new BufferSnapshot
            {
                CursorCol = buffer.CursorCol,
                CursorRow = buffer.CursorRow,
                IsAltScreen = buffer.IsAltScreenActive
            };

            // Capture all observable lines (Scrollback + Viewport)
            // or just Viewport? Usually for "Screen" snapshot we want Viewport.
            // But for "State" we might want scrollback too.
            // Let's capture Viewport for now as that's what user sees.

            var rows = buffer.ViewportRows;
            snapshot.Lines = rows.Select(r =>
            {
                // Simplified serialization: just text content + wrapping
                // Ideal: serialize cells with colors, but text is good start for basic determinism
                var sb = new StringBuilder();
                foreach (var cell in r.Cells)
                {
                    sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
                }
                return sb.ToString().TrimEnd(); // Trim for simpler comparison
            }).ToArray();

            return snapshot;
        }

        public string ToFormattedString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Cursor: ({CursorCol}, {CursorRow}) Alt: {IsAltScreen}");
            sb.AppendLine("--- Screen Content ---");
            for (int i = 0; i < Lines.Length; i++)
            {
                sb.AppendLine($"{i:D3}| {Lines[i]}");
            }
            return sb.ToString();
        }
    }
}
