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
