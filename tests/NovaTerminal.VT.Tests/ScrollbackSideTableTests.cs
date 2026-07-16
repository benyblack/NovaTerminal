using System;
using System.Collections.Generic;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// Regression coverage for the resize/reflow scrollback paths dropping row side
// tables (extended text, hyperlinks, wrap flag). The normal write-path eviction
// always preserved them; the height-shrink redistribution and the reflow
// rebuild appended bare cell arrays, so emoji / multi-codepoint graphemes came
// back corrupted from scrollback after any resize. Surfaced by agent-host
// readScrollback review (PR #184), but affects every scrollback consumer.
public class ScrollbackSideTableTests
{
    private static List<string> AllScrollbackExtendedText(TerminalBuffer buffer)
    {
        var values = new List<string>();
        for (int i = 0; i < buffer.Scrollback.Count; i++)
        {
            var map = buffer.Scrollback.GetExtendedTextMap(i);
            if (map == null) continue;
            for (int col = 0; col < buffer.Cols; col++)
            {
                if (map.TryGet(col, out var text) && text != null)
                {
                    values.Add(text);
                }
            }
        }
        return values;
    }

    [Fact]
    public void HeightShrink_PushesRowsToScrollback_WithExtendedTextPreserved()
    {
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("ok \U0001F44D done\r\n");
        parser.Process("plain row\r\n");

        // Height-only shrink: the top viewport rows (including the emoji row)
        // are redistributed into scrollback.
        buffer.Resize(20, 2);

        Assert.True(buffer.Scrollback.Count > 0);
        Assert.Contains("\U0001F44D", AllScrollbackExtendedText(buffer));
    }

    [Fact]
    public void ShrinkThenGrow_RestoresExtendedTextIntoTheViewport()
    {
        // The pull-from-scrollback grow path (Reshape) runs on the main screen
        // while the alternate screen is active — e.g. vim open during a height
        // shrink + grow. The emoji row must come back from scrollback with its
        // side table, not as HasExtendedText cells with no map behind them.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("ok \U0001F44D done\r\n");
        parser.Process("plain row\r\n");

        parser.Process("\x1b[?1049h");   // enter alt screen (vim et al.)
        buffer.Resize(20, 2);            // Reshape shrink: emoji row → scrollback
        Assert.Contains("\U0001F44D", AllScrollbackExtendedText(buffer));
        buffer.Resize(20, 6);            // Reshape grow: pulled back from scrollback
        parser.Process("\x1b[?1049l");   // back to the main screen

        var restored = new List<string>();
        foreach (var row in buffer.ViewportRows)
        {
            var map = row.GetExtendedTextMap();
            if (map == null) continue;
            for (int col = 0; col < buffer.Cols; col++)
            {
                if (map.TryGet(col, out var text) && text != null) restored.Add(text);
            }
        }
        Assert.Contains("\U0001F44D", restored);
    }

    [Fact]
    public void PopThenAppend_DoesNotResurrectStaleMetadata()
    {
        var pool = new NovaTerminal.VT.Storage.TerminalPagePool();
        var scrollback = new NovaTerminal.VT.Storage.ScrollbackPages(4, pool);
        var map = new NovaTerminal.VT.Storage.SmallMap<string>();
        map.Set(0, "\U0001F44D");
        scrollback.AppendRow(new TerminalCell[4], isWrapped: true, extendedText: map);

        // Pop returns the metadata with the row and vacates the slot…
        Assert.True(scrollback.TryPopLastRow(new TerminalCell[4], out var isWrapped, out var extendedText, out _));
        Assert.True(isWrapped);
        Assert.NotNull(extendedText);

        // …so a plain row appended into the same slot comes back clean: no
        // wrap flag, no extended text inherited from the popped row.
        long abs = scrollback.AppendRow(new TerminalCell[4]);
        int index = (int)(abs - scrollback.TotalRowsEvicted);
        Assert.Null(scrollback.GetExtendedTextMap(index));
        Assert.False(scrollback.IsRowWrapped(index));
    }

    [Fact]
    public void WidthReflow_RebuildsScrollback_WithExtendedTextPreserved()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("ok \U0001F44D done\r\n");
        for (int i = 0; i < 8; i++)
        {
            parser.Process($"filler-{i}\r\n");
        }
        // The emoji row has scrolled off via the write path (side tables intact).
        Assert.Contains("\U0001F44D", AllScrollbackExtendedText(buffer));

        // Width change triggers the reflow engine, which rebuilds scrollback
        // from reflowed rows; side tables must survive the rebuild.
        buffer.Resize(24, 4);

        Assert.Contains("\U0001F44D", AllScrollbackExtendedText(buffer));
    }
}
