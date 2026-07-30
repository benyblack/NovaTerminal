using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// #164 item 2a: the reflow engine flattened rows into a (Cell, ExtendedText) tuple, with no field
// for the hyperlink. It read hyperlinks *out* of the old rows and handed GetHyperlinkMap() to the
// rebuilt scrollback on the way out, but nothing in between ever populated the flowed rows - so
// that map was unconditionally null and every OSC 8 link died on the first resize.
//
// The two no-reflow resize paths (alt screen, detached screen buffers) had the same shape of bug:
// they rebuilt rows from Cells alone, dropping extended graphemes as well as links.
public class ReflowMetadataTests
{
    private const string Uri = "https://example.com/reflow";
    private const string OtherUri = "https://example.com/other";
    private const string ThumbsUp = "\U0001F44D";

    private static string?[] LinksOnRow(TerminalBuffer buffer, int viewRow, int cols)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count + viewRow);
            Assert.NotNull(row);
            var links = new string?[cols];
            for (int c = 0; c < cols; c++) links[c] = row!.GetHyperlink(c);
            return links;
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string GraphemesOnRow(TerminalBuffer buffer, int viewRow, int cols)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var sb = new System.Text.StringBuilder();
            for (int c = 0; c < cols; c++) sb.Append(buffer.GetGrapheme(c, viewRow));
            return sb.ToString();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Reflow_KeepsHyperlinkWhenALineWrapsOntoASecondRow()
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        // Ten linked characters, comfortably inside 12 columns.
        parser.Process($"\u001b]8;;{Uri}\u001b\\0123456789\u001b]8;;\u001b\\");

        // Narrow to 6: the logical line splits across two physical rows.
        buffer.Resize(6, 4);

        Assert.Equal(new[] { Uri, Uri, Uri, Uri, Uri, Uri }, LinksOnRow(buffer, 0, 6));
        Assert.Equal(new[] { Uri, Uri, Uri, Uri, null, null }, LinksOnRow(buffer, 1, 6));
    }

    [Fact]
    public void Reflow_KeepsHyperlinkWhenTwoRowsUnwrapIntoOne()
    {
        var buffer = new TerminalBuffer(6, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\u001b]8;;{Uri}\u001b\\0123456789\u001b]8;;\u001b\\");

        // Widen to 12: the wrapped pair rejoins into a single row.
        buffer.Resize(12, 4);

        var links = LinksOnRow(buffer, 0, 12);
        Assert.Equal(Uri, links[0]);
        Assert.Equal(Uri, links[9]);
        // The padding past the linked text is not part of the link.
        Assert.Null(links[10]);
        Assert.Null(links[11]);
    }

    [Fact]
    public void Reflow_KeepsAdjacentLinksDistinct()
    {
        // A single shared URI would pass even if reflow smeared one link across the whole row, so
        // use two: the boundary between them has to land in the right place after reflow.
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\u001b]8;;{Uri}\u001b\\aaaa\u001b]8;;{OtherUri}\u001b\\bbbb\u001b]8;;\u001b\\");

        buffer.Resize(4, 4);

        Assert.Equal(new[] { Uri, Uri, Uri, Uri }, LinksOnRow(buffer, 0, 4));
        Assert.Equal(new[] { OtherUri, OtherUri, OtherUri, OtherUri }, LinksOnRow(buffer, 1, 4));
    }

    [Fact]
    public void Reflow_KeepsExtendedGraphemesAlignedWithTheirLinks()
    {
        // Extended text already survived reflow; this pins the two side tables staying in step
        // with each other, which is the property the shared tuple now guarantees.
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\u001b]8;;{Uri}\u001b\\ab{ThumbsUp}cd\u001b]8;;\u001b\\");

        buffer.Resize(4, 4);

        // Row 0 is "ab" + the emoji's two columns; the emoji cluster must still read back whole,
        // and its lead column must still carry the link.
        Assert.Equal(ThumbsUp, GraphemesOnRow(buffer, 0, 4).Substring(2, 2).TrimEnd());
        Assert.Equal(Uri, LinksOnRow(buffer, 0, 4)[2]);
    }

    [Fact]
    public void AltScreenResize_KeepsHyperlinksAndExtendedGraphemes()
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        // Enter the alt screen (DECSET 1049), then write a linked emoji into it.
        parser.Process("\u001b[?1049h");
        parser.Process($"\u001b]8;;{Uri}\u001b\\a{ThumbsUp}b\u001b]8;;\u001b\\");

        // The alt screen is not reflowed - it is copied column-for-column - so growing the width
        // must carry the side tables across unchanged.
        buffer.Resize(16, 4);

        Assert.Equal(ThumbsUp, GraphemesOnRow(buffer, 0, 16).Substring(1, 2).TrimEnd());
        var links = LinksOnRow(buffer, 0, 16);
        Assert.Equal(Uri, links[0]);
        Assert.Equal(Uri, links[1]);
        Assert.Equal(Uri, links[3]);
        Assert.Null(links[4]);
    }

    [Fact]
    public void AltScreenResize_DropsMetadataForColumnsThatNoLongerExist()
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[?1049h");
        parser.Process($"\u001b]8;;{Uri}\u001b\\0123456789\u001b]8;;\u001b\\");

        // Shrink below the linked span: the surviving columns keep their link, and nothing may be
        // copied past the new width.
        buffer.Resize(4, 4);

        Assert.Equal(new[] { Uri, Uri, Uri, Uri }, LinksOnRow(buffer, 0, 4));
    }

    [Fact]
    public void Reflow_KeepsLinksOnTrailingSpacesInsideTheSpan()
    {
        // The trailing-content trim decides how much of a non-wrapped row enters the logical
        // stream, and it read only the cell: a blank cell that carries nothing but a hyperlink
        // looked like padding. Everything past that point is never re-emitted, so those columns
        // lost their link permanently on any width change.
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        // The line must NOT be the cursor row: the trim has a special case that extends validLen
        // up to the cursor column, which would mask the drop. A newline moves off it.
        parser.Process($"\u001b]8;;{Uri}\u001b\\ab   \u001b]8;;\u001b\\\r\nnext");

        buffer.Resize(8, 4);

        var links = LinksOnRow(buffer, 0, 8);
        Assert.Equal(Uri, links[0]);
        Assert.Equal(Uri, links[1]);
        Assert.Equal(Uri, links[4]);   // the last linked space
        Assert.Null(links[5]);         // genuine padding past the span
    }

    [Fact]
    public void MainScreenKeepsMetadataAcrossAResizeTakenWhileTheAltScreenIsUp()
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\u001b]8;;{Uri}\u001b\\a{ThumbsUp}b\u001b]8;;\u001b\\");

        // Covers the alt-screen round trip: the main screen is detached while the alt screen is
        // up, and the resize reflows it in the background.
        //
        // It does NOT reach ResizeDetachedScreenBufferNoLock, which only runs on the way back if
        // _mainScreen geometry is still stale - the background resize keeps it in step. Mutation-
        // checking confirmed that: reverting that method's metadata copy leaves every test here
        // green. Its fix is carried on correctness grounds only.
        parser.Process("\u001b[?1049h");
        buffer.Resize(16, 4);
        parser.Process("\u001b[?1049l");

        Assert.Equal(ThumbsUp, GraphemesOnRow(buffer, 0, 16).Substring(1, 2).TrimEnd());
        Assert.Equal(Uri, LinksOnRow(buffer, 0, 16)[0]);
    }

    [Fact]
    public void CopyRowMetadataFrom_IgnoresColumnsAtOrBeyondTheLimit()
    {
        var source = new TerminalRow(8);
        source.SetExtendedText(1, ThumbsUp);
        source.SetHyperlink(1, Uri);
        source.SetExtendedText(6, ThumbsUp);
        source.SetHyperlink(6, OtherUri);

        var target = new TerminalRow(4);
        target.CopyRowMetadataFrom(source, 4);

        Assert.Equal(ThumbsUp, target.GetExtendedText(1));
        Assert.Equal(Uri, target.GetHyperlink(1));
        // Column 6 is outside the target's width; copying it would key the map past the row.
        Assert.Null(target.GetExtendedText(6));
        Assert.Null(target.GetHyperlink(6));
    }

    [Fact]
    public void CopyRowMetadataFrom_IsANoOpForAPlainSourceRow()
    {
        var source = new TerminalRow(8);
        var target = new TerminalRow(8);

        target.CopyRowMetadataFrom(source, 8);

        Assert.False(target.HasRowMetadata);
    }
}
