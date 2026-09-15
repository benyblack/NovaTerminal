using NovaTerminal.VT;
using NovaTerminal.VT.Links;

namespace NovaTerminal.VT.Tests.Links;

public class RowTextExtractorTests
{
    [Fact]
    public void Extracts_plain_ascii_with_identity_column_map()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 1);
        var parser = new AnsiParser(buffer);
        parser.Process("ab https://x.io");

        var (text, map) = RowTextExtractor.Extract(buffer, absRow: 0);

        Assert.StartsWith("ab https://x.io", text);
        // First 15 characters map to columns 0..14 (1:1 for single-width ASCII).
        for (int i = 0; i < 15; i++) Assert.Equal(i, map[i]);
    }

    [Fact]
    public void SpanToColumns_maps_char_range_to_inclusive_columns()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 1);
        var parser = new AnsiParser(buffer);
        parser.Process("ab https://x.io");

        var (_, map) = RowTextExtractor.Extract(buffer, absRow: 0);
        // "https://x.io" occupies chars [3, 15) -> columns 3..14 inclusive.
        var (startCol, endCol) = RowTextExtractor.SpanToColumns(new LinkSpan(3, 15, "https://x.io"), map);

        Assert.Equal(3, startCol);
        Assert.Equal(14, endCol);
    }

    [Fact]
    public void Wide_char_continuation_column_is_skipped_in_map()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 1);
        var parser = new AnsiParser(buffer);
        // A wide CJK glyph occupies two columns (0 = glyph, 1 = continuation), then "x".
        parser.Process("中x");

        var (text, map) = RowTextExtractor.Extract(buffer, absRow: 0);

        Assert.Equal('中', text[0]);
        Assert.Equal(0, map[0]);   // wide glyph at column 0
        Assert.Equal('x', text[1]);
        Assert.Equal(2, map[1]);   // 'x' lands at column 2 (column 1 is the continuation cell)
    }
}
