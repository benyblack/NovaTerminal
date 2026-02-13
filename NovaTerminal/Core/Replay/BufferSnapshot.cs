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
                var sb = new StringBuilder();
                for (int i = 0; i < r.Cells.Length; i++)
                {
                    var cell = r.Cells[i];
                    if (cell.IsWideContinuation) continue;

                    string text = (cell.HasExtendedText ? r.GetExtendedText(i) : null) ?? (cell.Character == '\0' ? " " : cell.Character.ToString());
                    sb.Append(text);
                }
                return sb.ToString().TrimEnd();
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
