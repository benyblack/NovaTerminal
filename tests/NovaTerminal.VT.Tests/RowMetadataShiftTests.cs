using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// #164 item 1: ICH (CSI @), DCH (CSI P) and insert mode (IRM) shifted TerminalRow.Cells but left
// the per-row side tables - extended graphemes and OSC 8 hyperlinks - keyed to their pre-shift
// columns. The result was visibly wrong output: a cell kept its HasExtendedText flag while the
// string describing it stayed behind at the old column, so an emoji/CJK cluster rendered (and
// copied) as its first UTF-16 code unit while a neighbouring plain cell rendered the cluster.
// Hyperlinks drifted the same way, so the clickable region no longer matched the visible text.
public class RowMetadataShiftTests
{
    private const string ThumbsUp = "\U0001F44D"; // astral, width 2: lead cell + continuation
    private const string Uri = "https://example.com/";
    // #95 gap 2: hyperlinks are an identity now, not a bare URI. Build them through the registry, the
    // same way the parser does, rather than reaching for an internal constructor.
    private static readonly NovaTerminal.VT.Links.HyperlinkRegistry Registry = new();
    private static NovaTerminal.VT.Links.Hyperlink Link(string uri) => Registry.Resolve(null, uri)!;

    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal(int cols = 20, int rows = 3)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer));
    }

    /// GetGrapheme requires the read lock; every assertion here goes through this.
    private static string GraphemeAt(TerminalBuffer buffer, int col)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGrapheme(col, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// Every extended-text entry still held by row 0's side table, keyed by column.
    ///
    /// The drop cases have to assert against the map itself, not against GetGrapheme: a stale
    /// entry is invisible through the rendered view whenever the cell that lands on the column
    /// carries no HasExtendedText flag. It becomes visible - as the wrong glyph - as soon as some
    /// later write does set that flag, which is exactly the corruption #164 describes.
    private static (int Col, string Text)[] SideTableTexts(TerminalBuffer buffer, int cols)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(0);
            Assert.NotNull(row);
            var found = new List<(int, string)>();
            for (int c = 0; c < cols; c++)
            {
                string? text = row!.GetExtendedText(c);
                if (text != null) found.Add((c, text));
            }
            return found.ToArray();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string?[] SideTableLinks(TerminalBuffer buffer, int cols)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(0);
            Assert.NotNull(row);
            var links = new string?[cols];
            for (int c = 0; c < cols; c++) links[c] = row!.GetHyperlink(c)?.Uri;
            return links;
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Ich_MovesExtendedGraphemeWithItsCell()
    {
        var (buffer, parser) = NewTerminal();
        // Columns: 0 'a', 1-2 emoji (lead + continuation), 3 'b'.
        parser.Process($"a{ThumbsUp}b");
        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 1));

        // Home, then insert two blanks: everything shifts right by two.
        parser.Process("\u001b[1G\u001b[2@");

        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 3));
        // The stale key is the bug's signature: before the fix the string stayed at column 1.
        Assert.NotEqual(ThumbsUp, GraphemeAt(buffer, 1));
        Assert.Equal("a", GraphemeAt(buffer, 2));
        Assert.Equal("b", GraphemeAt(buffer, 5));
    }

    [Fact]
    public void Dch_MovesExtendedGraphemeWithItsCell()
    {
        var (buffer, parser) = NewTerminal();
        // Columns: 0 'a', 1 'b', 2-3 emoji, 4 'c'.
        parser.Process($"ab{ThumbsUp}c");

        // Home, delete one character: everything shifts left by one.
        parser.Process("\u001b[1G\u001b[1P");

        Assert.Equal("b", GraphemeAt(buffer, 0));
        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 1));
        Assert.NotEqual(ThumbsUp, GraphemeAt(buffer, 2));
        Assert.Equal("c", GraphemeAt(buffer, 3));
    }

    [Fact]
    public void Dch_DropsMetadataForTheDeletedColumns()
    {
        var (buffer, parser) = NewTerminal(cols: 10);
        parser.Process($"a{ThumbsUp}b");

        // Cursor onto the emoji lead cell, then delete both of its columns.
        parser.Process("\u001b[2G\u001b[2P");

        // The cluster is gone from the side table entirely - not relocated, and not left behind
        // at column 1 where the pre-shift key pointed.
        Assert.Empty(SideTableTexts(buffer, 10));
        Assert.Equal("b", GraphemeAt(buffer, 1));
    }

    [Fact]
    public void Ich_DropsMetadataPushedPastTheEndOfTheRow()
    {
        var (buffer, parser) = NewTerminal(cols: 6);
        // Emoji at columns 4-5, hard against the right edge.
        parser.Process($"abcd{ThumbsUp}");
        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 4));

        // Insert three blanks at home: the emoji is pushed off the row.
        parser.Process("\u001b[1G\u001b[3@");

        // Column 4 now holds a plain 'b', so a stale entry would not show through GetGrapheme -
        // it has to be gone from the map itself.
        Assert.Empty(SideTableTexts(buffer, 6));
    }

    // The next two are regression guards on the shift's range check rather than on the original
    // bug: they pass both before and after the fix, but fail against the obvious wrong
    // implementation that range-checks unshifted columns (below startCol) against startCol and so
    // discards every entry to the left of the cursor.
    [Fact]
    public void Ich_LeavesColumnsBeforeTheCursorAlone()
    {
        var (buffer, parser) = NewTerminal();
        // Emoji at columns 0-1; the cursor sits to its right, so it must not move.
        parser.Process($"{ThumbsUp}xy");
        parser.Process("\u001b[4G\u001b[2@");

        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 0));
        Assert.Equal(new[] { (0, ThumbsUp) }, SideTableTexts(buffer, 20));
    }

    [Fact]
    public void Dch_LeavesColumnsBeforeTheCursorAlone()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process($"{ThumbsUp}xy");
        parser.Process("\u001b[4G\u001b[1P");

        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 0));
        Assert.Equal(new[] { (0, ThumbsUp) }, SideTableTexts(buffer, 20));
    }

    [Fact]
    public void Ich_MovesHyperlinkWithItsCell()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process($"\u001b]8;;{Uri}\u001b\\ab\u001b]8;;\u001b\\");
        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(0, 0));

        parser.Process("\u001b[1G\u001b[3@");

        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(3, 0));
        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(4, 0));
        // The inserted blanks are not part of the link.
        Assert.Null(buffer.GetHyperlinkAbsolute(0, 0));
        Assert.Null(buffer.GetHyperlinkAbsolute(2, 0));
    }

    [Fact]
    public void Dch_MovesHyperlinkWithItsCell()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process($"xx\u001b]8;;{Uri}\u001b\\ab\u001b]8;;\u001b\\");
        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(2, 0));

        parser.Process("\u001b[1G\u001b[2P");

        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(0, 0));
        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(1, 0));
        Assert.Null(buffer.GetHyperlinkAbsolute(2, 0));
    }

    [Fact]
    public void InsertMode_MovesExtendedGraphemeWithItsCell()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process($"a{ThumbsUp}b");

        // IRM on, home, type one character: the printable itself shifts the row right by one.
        // This is the per-character path, hence the hot-path guard in InsertCharactersInternal.
        parser.Process("\u001b[4h\u001b[1GZ");

        Assert.Equal("Z", GraphemeAt(buffer, 0));
        Assert.Equal("a", GraphemeAt(buffer, 1));
        Assert.Equal(ThumbsUp, GraphemeAt(buffer, 2));
        Assert.NotEqual(ThumbsUp, GraphemeAt(buffer, 1));
    }

    [Fact]
    public void InsertMode_MovesHyperlinkWithItsCell()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process($"\u001b]8;;{Uri}\u001b\\ab\u001b]8;;\u001b\\");

        parser.Process("\u001b[4h\u001b[1GZ");

        // 'Z' is typed outside any OSC 8 span, so it must not inherit the link.
        Assert.Null(buffer.GetHyperlinkAbsolute(0, 0));
        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(1, 0));
        Assert.Equal(Uri, buffer.GetHyperlinkAbsolute(2, 0));
    }

    [Fact]
    public void Ich_WithCountBeyondTheRowClearsEverythingFromTheCursor()
    {
        var (buffer, parser) = NewTerminal(cols: 8);
        parser.Process($"a{ThumbsUp}b");

        // A count larger than the remaining width blanks the tail; no metadata may survive it.
        parser.Process("\u001b[2G\u001b[9999@");

        // Only the emoji ever had an extended-text entry ('a' is plain ASCII), and it was pushed
        // out, so nothing may remain.
        Assert.Empty(SideTableTexts(buffer, 8));
        Assert.Equal("a", GraphemeAt(buffer, 0));
    }

    [Fact]
    public void Dch_ClearsLinksFromTheBlankedTailColumns()
    {
        var (buffer, parser) = NewTerminal(cols: 6);
        parser.Process($"\u001b]8;;{Uri}\u001b\\abcdef\u001b]8;;\u001b\\");
        Assert.All(SideTableLinks(buffer, 6), link => Assert.Equal(Uri, link));

        // Delete two columns at home: the two vacated tail columns are blank cells, so they must
        // not still be part of the link.
        parser.Process("\u001b[1G\u001b[2P");

        Assert.Equal(new[] { Uri, Uri, Uri, Uri, null, null }, SideTableLinks(buffer, 6));
    }

    [Fact]
    public void ShiftRowMetadata_IsANoOpForRowsWithoutSideTables()
    {
        // The guard in the write path skips the shift entirely for plain rows; make sure the
        // method itself is also safe if called directly on one.
        var row = new TerminalRow(10);
        Assert.False(row.HasRowMetadata);

        row.ShiftRowMetadata(0, 3, 10);

        Assert.False(row.HasRowMetadata);
        Assert.Null(row.GetExtendedText(0));
        Assert.Null(row.GetHyperlink(0));
    }

    [Fact]
    public void ShiftRowMetadata_DropsTheMapOnceEveryEntryIsShiftedOut()
    {
        // A row that shifts all of its entries off the end must end up with no maps at all,
        // not with empty ones - HasRowMetadata is the write path's fast-path guard.
        var row = new TerminalRow(4);
        row.SetExtendedText(3, ThumbsUp);
        row.SetHyperlink(3, Link(Uri));
        Assert.True(row.HasRowMetadata);

        row.ShiftRowMetadata(0, 4, 4);

        Assert.False(row.HasRowMetadata);
    }
}
