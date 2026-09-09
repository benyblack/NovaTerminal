using System;
using System.Collections.Generic;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// Kitty in-band resize (DEC private mode 2048) and the CSI 14 t pixel-size report — the two
/// geometry-announcement channels terminal-browser depends on (it has no SIGWINCH on Windows
/// and never polls console size). Reports go to the child's stdin, so their exact wire shape
/// is asserted, and nothing may be emitted while mode 2048 is unset.
/// </summary>
public class InBandResizeTests
{
    private static AnsiParser CreateParser(out List<string> responses)
    {
        var buffer = new TerminalBuffer(86, 30);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true);
        var list = new List<string>();
        parser.OnResponse = r => list.Add(r);
        responses = list;
        return parser;
    }

    [Fact]
    public void Mode2048_TracksEnableAndDisable()
    {
        var parser = CreateParser(out _);
        Assert.False(parser.InBandResizeReportsEnabled);

        parser.Process("\x1b[?2048h");
        Assert.True(parser.InBandResizeReportsEnabled);

        parser.Process("\x1b[?2048l");
        Assert.False(parser.InBandResizeReportsEnabled);
    }

    [Fact]
    public void Decrqm_2048_ReportsRecognizedWithLiveState()
    {
        var parser = CreateParser(out var responses);

        parser.Process("\x1b[?2048$p"); // not recognized yet
        Assert.Contains(responses, r => r == "\x1b[?2048;2$y");

        parser.Process("\x1b[?2048h");
        parser.Process("\x1b[?2048$p");
        Assert.Contains(responses, r => r == "\x1b[?2048;1$y");
    }

    [Fact]
    public void SendInBandResize_WhileDisabled_EmitsNothing()
    {
        var parser = CreateParser(out var responses);

        parser.SendInBandResize(30, 86, widthPx: 975, heightPx: 720);

        Assert.Empty(responses);
    }

    [Fact]
    public void SendInBandResize_WhileEnabled_EmitsKittyReport()
    {
        var parser = CreateParser(out var responses);
        parser.Process("\x1b[?2048h");
        responses.Clear();

        parser.SendInBandResize(30, 86, widthPx: 975, heightPx: 720);

        var response = Assert.Single(responses);
        Assert.Equal("\x1b[48;30;86;720;975t", response);
    }

    [Fact]
    public void SendInBandResize_AfterModeReset_EmitsNothing()
    {
        var parser = CreateParser(out var responses);
        parser.Process("\x1b[?2048h");
        parser.Process("\x1b[?2048l");
        responses.Clear();

        parser.SendInBandResize(30, 86, 975, 720);

        Assert.Empty(responses);
    }

    [Fact]
    public void Csi14t_ReportsPixelSizeFromCellMetrics()
    {
        var parser = CreateParser(out var responses);
        parser.CellWidth = 10f;
        parser.CellHeight = 20f;

        parser.Process("\x1b[14t");

        var response = Assert.Single(responses);
        // 86 cols * 10 = 860 px wide; 30 rows * 20 = 600 px tall; order is height then width.
        Assert.Equal("\x1b[4;600;860t", response);
    }

    [Fact]
    public void Csi14t_WithSubParams_StillReports()
    {
        // Some clients send CSI > 14 t or CSI 14 ; 1 t — the report is the same question.
        var parser = CreateParser(out var responses);
        parser.CellWidth = 10f;
        parser.CellHeight = 20f;

        parser.Process("\x1b[14;1t");

        var response = Assert.Single(responses);
        Assert.Equal("\x1b[4;600;860t", response);
    }

    [Fact]
    public void Csi_OtherWindowOps_StaySilent()
    {
        var parser = CreateParser(out var responses);
        parser.CellWidth = 10f;
        parser.CellHeight = 20f;

        // 22;0 = push icon title, 5 = raise window: no reports defined for these.
        parser.Process("\x1b[22;0t");
        parser.Process("\x1b[5t");

        Assert.Empty(responses);
    }
}
