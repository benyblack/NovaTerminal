using NovaTerminal.VT;

namespace NovaTerminal.Tests.Infra;

/// <summary>
/// What a pane actually shows: the viewport rendered as plain text, for asserting on banners and
/// other terminal output. The buffer's viewport is private, hence the reflection.
/// </summary>
internal static class TerminalBufferText
{
    public static string Visible(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField(
            "_viewport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var viewport = (TerminalRow[])field!.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(RowText)).TrimEnd();
    }

    private static string RowText(TerminalRow row)
    {
        char[] chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }
}
