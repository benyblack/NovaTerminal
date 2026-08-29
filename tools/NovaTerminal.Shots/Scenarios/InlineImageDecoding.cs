using Avalonia;
using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using SkiaSharp;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// Shared machinery for <see cref="SixelGraphicsScenario"/> and
/// <see cref="Iterm2InlineImageScenario"/>: wiring a real decoder onto the pane's parser, and
/// verifying afterward that the decoded picture actually reached the screen.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AnsiParser.ImageDecoder"/> is a public, settable dependency the parser was built
/// to take (<c>HandleSixel</c> and <c>HandleITerm2Image</c> both early-return when it is null),
/// but nothing under <c>src/NovaTerminal.App</c> ever assigns it —
/// <c>TerminalPane.CreateAndWireParser</c> constructs a bare <c>new AnsiParser(Buffer)</c> and
/// stops there. The only other assignments in the repository are test doubles (see
/// <c>AnsiParserHardeningTests.RecordingImageDecoder</c>). So a plain build parses both escape
/// sequences correctly — DCS sixel framing, OSC 1337's <c>File=</c> parameters, the base64
/// payload — but never turns the bytes into pixels: no shipped build renders either protocol
/// today. See the Task 13 report for the full trail (grep results, line numbers) and why fixing
/// this for real belongs in <c>src/</c>, which this task may not touch.
/// </para>
/// <para>
/// <see cref="EnableRealDecoding"/> supplies that missing piece from the harness instead, through
/// the same public seam the tests use, so both scenarios still exercise genuine production code
/// for everything downstream of "bytes in hand": <c>TerminalBuffer.AddImage</c>, scrollback
/// anchoring, and <c>TerminalDrawOperation</c>'s <c>SKBitmap</c> compositing are all untouched.
/// The decoder itself is real too — <see cref="NovaTerminal.Rendering.SixelDecoder"/> (already
/// shipped, just never instantiated) for sixel, and Skia's own image decoder for iTerm2's
/// arbitrary bytes — not a canned bitmap standing in for either file.
/// </para>
/// </remarks>
internal static class InlineImageDecoding
{
    /// <summary>
    /// Gives <paramref name="pane"/>'s parser a real decoder, if it does not have one already.
    /// Must run before the command that emits the image, since the parser reads
    /// <see cref="AnsiParser.ImageDecoder"/> at decode time, not lazily.
    /// </summary>
    public static void EnableRealDecoding(TerminalPane pane)
    {
        AnsiParser parser = pane.Parser
            ?? throw new InvalidOperationException("The pane has no parser yet.");

        parser.ImageDecoder ??= new ShotsImageDecoder();
    }

    /// <summary>
    /// Fails loudly unless a real, decoded picture is on screen where the terminal buffer says
    /// the most recently placed image lives — not just "some frame was captured". A terminal
    /// that silently dropped the escape sequence (the <c>ImageDecoder == null</c> early-return,
    /// or any other decode failure) leaves that exact rectangle at the pane's uniform background
    /// colour, which <see cref="Rasterizer.InkFraction"/> reports as ~0 ink; a genuinely decoded
    /// picture — the plot or the logo, both are colourful and non-uniform by construction — does
    /// not.
    /// </summary>
    public static void AssertImageRegionDecoded(ShotContext context, TerminalPane pane, string protocolLabel)
    {
        TerminalBuffer buffer = pane.Buffer
            ?? throw new InvalidOperationException("The pane has no buffer.");

        if (buffer.Images.Count == 0)
        {
            throw new InvalidOperationException(
                $"{protocolLabel}: no image was ever added to the terminal buffer. The escape " +
                "sequence's own framing (DCS/OSC introducer and terminator) is still consumed " +
                "either way, so this is not stray text on screen - it is silence where a decoded " +
                "picture should be.");
        }

        TerminalImage image = buffer.Images[^1];

        var view = context.Driver.RequireIn<TerminalView>(pane, "TermView");
        CellMetrics metrics = view.Metrics;

        if (metrics.CellWidth <= 0 || metrics.CellHeight <= 0)
        {
            throw new InvalidOperationException(
                $"{protocolLabel}: TermView has no cell metrics yet, so the image's on-screen " +
                "rectangle cannot be computed.");
        }

        // TerminalImage.CellY is an absolute buffer row (scrollback + viewport), stamped once at
        // placement time by AnsiParser (see HandleSixel/HandleITerm2Image); TerminalView.ScrollOffset
        // and TotalLines answer "which absolute row is currently at the top of the viewport" the
        // same way TerminalView's own hit-testing does (OnSizeChanged's displayStart, and the drag-
        // autoscroll path). Both scenarios never scroll, so ScrollOffset is 0 and this always points
        // at the still-visible bottom of the transcript.
        int displayStart = Math.Max(0, buffer.TotalLines - buffer.Rows - view.ScrollOffset);
        int rowInViewport = image.CellY - displayStart;

        Point originInWindow = view.TranslatePoint(new Point(0, 0), context.Window)
            ?? throw new InvalidOperationException(
                $"{protocolLabel}: TermView could not be located within the window.");

        // Captured at scale 1.0, deliberately, so this bitmap's pixels line up 1:1 with the
        // logical (DIP) coordinates TranslatePoint and CellMetrics both work in - a captured-at-
        // Run.Scale frame would need every measurement below multiplied by that scale too.
        using SKBitmap frame = Rasterizer.CaptureWindow(context.Window, 1.0);

        var wanted = SKRectI.Create(
            (int)Math.Round(originInWindow.X + (image.CellX * metrics.CellWidth)),
            (int)Math.Round(originInWindow.Y + (rowInViewport * metrics.CellHeight)),
            (int)Math.Round(image.CellWidth * metrics.CellWidth),
            (int)Math.Round(image.CellHeight * metrics.CellHeight));

        SKRectI region = SKRectI.Intersect(wanted, new SKRectI(0, 0, frame.Width, frame.Height));

        if (region.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{protocolLabel}: the image's computed region {wanted} does not overlap the " +
                $"captured {frame.Width}x{frame.Height} frame at all - the row/column math above " +
                "is wrong, not the decode.");
        }

        using var cropped = new SKBitmap(region.Width, region.Height);
        frame.ExtractSubset(cropped, region);

        double ink = Rasterizer.InkFraction(cropped);
        if (ink <= 0.05)
        {
            throw new InvalidOperationException(
                $"{protocolLabel}: the image region {region} is {ink:P1} ink - the inline image " +
                "did not decode. A decoded picture is not a uniform rectangle; this one is, which " +
                "is what a terminal that dropped the escape sequence and left background in its " +
                "place would also look like.");
        }
    }
}
