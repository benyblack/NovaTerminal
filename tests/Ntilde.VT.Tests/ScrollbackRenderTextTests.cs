using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// #164 item 4: the paged-scrollback branch of the render-snapshot builder hard-coded
// `Text = null, // TODO Step 5`, so any grapheme wider than one UTF-16 code unit was *drawn* as
// its first code unit once the line scrolled off screen - an emoji became a lone unpaired
// surrogate, and a combining sequence lost its marks.
//
// The read side was fixed earlier (#95 gap 1) by teaching GetGraphemeAbsolute the same side table,
// which is why selection and copy were already correct while the screen was visibly wrong. This
// covers the drawing half. The viewport path (PopulateRenderCellsFromRow_NoLock) was always
// correct, so the bug appeared only after a line scrolled.
public class ScrollbackRenderTextTests
{
    private const string ThumbsUp = "\U0001F44D";  // astral: 2 UTF-16 code units, width 2
    private const string EAcute = "é";       // 'e' + combining acute: 2 code units, width 1

    private const int Cols = 20;
    private const int Rows = 3;

    /// Writes <paramref name="firstLine"/> then enough filler to push it into scrollback, and
    /// returns the render snapshot's row 0 - which is therefore a *paged scrollback* row.
    private static RenderCellSnapshot[] ScrolledOffRowCells(string firstLine)
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        parser.Process(firstLine + "\r\n");
        for (int i = 0; i < Rows; i++) parser.Process($"filler {i}\r\n");

        Assert.True(buffer.Scrollback.Count > 0, "first row should have scrolled off");

        var request = new RenderSnapshotRequest
        {
            ViewportCols = Cols,
            ViewportRows = Rows,
            // Scroll back far enough that the scrolled-off line is the top visible row.
            ScrollOffset = buffer.Scrollback.Count
        };

        using var snapshot = buffer.CaptureRenderSnapshot(request, out _);
        Assert.True(snapshot.RowsData.Length > 0);
        // Copy out: the snapshot's arrays go back to the pool on Dispose.
        return snapshot.RowsData.Array[0].Cells.ToArray();
    }

    [Fact]
    public void ScrollbackRow_RendersAnAstralGraphemeInFull()
    {
        // "ok <emoji> done" - the emoji lead cell is column 3.
        var cells = ScrolledOffRowCells($"ok {ThumbsUp} done");

        Assert.Equal(ThumbsUp, cells[3].Text);
        // Character alone is the high surrogate, which is exactly what the renderer used to draw.
        Assert.True(char.IsHighSurrogate(cells[3].Character));
        Assert.True(cells[3].IsWide);
        Assert.True(cells[4].IsWideContinuation);
    }

    [Fact]
    public void ScrollbackRow_RendersACombiningSequenceInFull()
    {
        var cells = ScrolledOffRowCells($"caf{EAcute}");

        Assert.Equal(EAcute, cells[3].Text);
    }

    [Fact]
    public void ScrollbackRow_LeavesPlainCellsWithoutText()
    {
        // The fallback matters more than the fix: almost every cell is plain, and a non-null Text
        // on one would send the renderer down its slow shaped-run path for nothing.
        var cells = ScrolledOffRowCells("plain ascii text");

        Assert.All(cells, cell => Assert.Null(cell.Text));
    }

    [Fact]
    public void ScrollbackRow_LeavesNonGraphemeColumnsAlone()
    {
        // Only the lead cell of the cluster carries Text; the continuation column and the
        // surrounding plain cells must not.
        var cells = ScrolledOffRowCells($"ok {ThumbsUp} done");

        Assert.Null(cells[2].Text);
        Assert.Null(cells[4].Text);   // wide continuation
        Assert.Null(cells[5].Text);
    }
}
