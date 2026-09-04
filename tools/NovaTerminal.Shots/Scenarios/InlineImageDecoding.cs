using Avalonia;
using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using SkiaSharp;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// Shared verification for <see cref="SixelGraphicsScenario"/> and
/// <see cref="Iterm2InlineImageScenario"/>: that a decoded picture actually reached the screen, and
/// that it landed without wrecking the text around it.
/// </summary>
/// <remarks>
/// <para>
/// Both checks exist because both have failed in production, and neither is caught by anything else
/// the harness runs. A terminal that drops the escape sequence leaves the image's rectangle at the
/// pane background, which the blank-raster guard never notices because the surrounding transcript is
/// full of ink; and an image that decodes perfectly while the prompt beside it is indented or wrapped
/// mid-word is not a publishable picture either.
/// </para>
/// <para>
/// Historically these scenarios were unregistered because <c>src/</c> assigned no
/// <see cref="AnsiParser.ImageDecoder"/> at all, and an earlier version of this harness injected one
/// - making the screenshots demonstrate a capability no shipped build had. That injection was
/// removed, and production has since wired a real decoder, so nothing is injected here now: by the
/// time either scenario runs, <c>pane.Parser.ImageDecoder</c> is already set by the application.
/// </para>
/// </remarks>
internal static class InlineImageDecoding
{
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

        AssertTextResumesAtColumnZero(buffer, image, protocolLabel);

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
    /// <summary>
    /// Fails unless the first line of text below the image starts at column 0.
    /// </summary>
    /// <remarks>
    /// Decoding is only half of "rendered inline". Both Intents also require the picture to sit
    /// <i>correctly positioned relative to the surrounding text</i>, and that half fails today even
    /// though the decode succeeds: the cursor is not returned to column 0 after an image is placed,
    /// so the shell's next prompt begins partway across the row the image ended on. For sixel that
    /// is a visible indent; for the narrower iTerm2 logo the prompt starts far enough right to
    /// overrun the last column and wrap mid-word, splitting "(feat/sixel-decoder)" across two lines.
    ///
    /// <see cref="AssertImageRegionDecoded"/> cannot catch it - the image region itself is perfectly
    /// good ink - and neither can the blank-raster guard. Without this check both scenarios report
    /// success while producing an image no one would publish, which is the same shape of silent
    /// wrongness that shipped an empty hero-split pane.
    /// </remarks>
    private static void AssertTextResumesAtColumnZero(TerminalBuffer buffer, TerminalImage image, string protocolLabel)
    {
        // Scans from the image's own first row, not from the row below it. The cursor is not
        // always left past the image: for the iTerm2 logo the prompt resumes on the image's *last*
        // row (column 54 of a 116-column pane) and wraps from there, so a scan starting below the
        // picture walks straight past the offending line and onto its column-0 continuation, and
        // reports success. Text on any row the image occupies is itself the defect - both scenarios
        // emit their image from a command of its own, so nothing legitimate shares those rows.
        int firstRowBelow = image.CellY;

        for (int row = firstRowBelow; row < buffer.TotalLines; row++)
        {
            int viewportRow = row - Math.Max(0, buffer.TotalLines - buffer.Rows);
            if (viewportRow < 0 || viewportRow >= buffer.ViewportRows.Count)
            {
                continue;
            }

            TerminalCell[] cells = buffer.ViewportRows[viewportRow].Cells;
            int firstInked = Array.FindIndex(cells, c => c.Character != ' ' && !char.IsWhiteSpace(c.Character));

            if (firstInked < 0)
            {
                continue;
            }

            if (firstInked > 0)
            {
                throw new InvalidOperationException(
                    $"{protocolLabel}: the first text below the image starts at column {firstInked}, " +
                    "not column 0, so the cursor was left mid-row after the image was placed. The " +
                    "picture decoded, but the prompt after it is indented (and, if it starts far " +
                    "enough right, wraps mid-word) - the Intent requires the image correctly " +
                    "positioned relative to the surrounding text, not merely present.");
            }

            return;
        }
    }
}
