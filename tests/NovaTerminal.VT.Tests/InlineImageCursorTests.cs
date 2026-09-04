using System;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

// Regression tests for #405: after an inline image is placed, the cursor must be at column 0 of
// the row below the picture, so whatever the program writes next — in practice the shell's next
// prompt — starts at the left margin.
//
// All three image protocols reserved the image's cells by writing spaces row by row and
// deliberately omitted the newline on the last row, leaving the cursor at `CellX + width` on the
// image's own final row. The next prompt therefore resumed part-way across that row: an indent for
// a narrow image, and for a wide one an overrun past the last column that wrapped the prompt
// mid-word. iTerm2 made it worse by also setting the swallow-next-newline flag, eating the very
// newline the shell sent to get itself onto a fresh line.
public class InlineImageCursorTests
{
    private const int Cols = 40;
    private const int Rows = 20;

    // 30x40 pixels against the parser's default 10x20 cell = 3 cells wide, 2 tall. Small enough to
    // sit well inside the buffer, and not square, so a width/height mix-up cannot pass by accident.
    private const int PixelWidth = 30;
    private const int PixelHeight = 40;

    [Fact]
    public void Sixel_leaves_the_cursor_at_column_zero_below_the_image()
    {
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal();

        parser.Process("\x1bPq#0;2;0;0;0#0~~~\x1b\\");

        AssertCursorBelowImageAtColumnZero(buffer, "sixel");
    }

    [Fact]
    public void Iterm2_image_leaves_the_cursor_at_column_zero_below_the_image()
    {
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal();

        parser.Process($"\x1b]1337;File=inline=1:{FakePayload()}\x07");

        AssertCursorBelowImageAtColumnZero(buffer, "iterm2");
    }

    [Fact]
    public void Kitty_image_leaves_the_cursor_at_column_zero_below_the_image()
    {
        // forceConPtyFiltering: false, so this exercises the placement path on every platform. A
        // parser that believes ConPTY is filtering drops non-tunneled Kitty sequences before they
        // reach placement at all, which on Windows would leave this test silently asserting nothing.
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal(forceConPtyFiltering: false);

        parser.Process($"\x1b_Ga=T,f=100;{FakePayload()}\x1b\\");

        AssertCursorBelowImageAtColumnZero(buffer, "kitty");
    }

    // The behaviour the cursor position exists to produce, stated the way a user meets it: the text
    // written after an image starts at the left margin rather than beside the picture. Sixel is the
    // case that reaches WriteChar directly; the two OSC/APC protocols additionally set the
    // swallow-next-newline flag, so they are covered separately below.
    [Fact]
    public void Text_after_a_sixel_image_starts_at_the_left_margin()
    {
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal();

        parser.Process("\x1bPq#0;2;0;0;0#0~~~\x1b\\");
        parser.Process("after");

        Assert.Equal("a", GraphemeAt(buffer, col: 0, viewRow: CursorViewRow(buffer)));
    }

    // A program that emits an image normally follows it with its own newline. That newline is
    // swallowed (the flag exists for exactly this) so the image is not followed by a blank line —
    // and with the cursor already parked at column 0 of the next row, the following text still
    // lands at the left margin rather than a row further down.
    [Fact]
    public void A_trailing_newline_after_an_iterm2_image_does_not_add_a_blank_line()
    {
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal();

        parser.Process($"\x1b]1337;File=inline=1:{FakePayload()}\x07");
        int rowAfterImage = CursorViewRow(buffer);

        parser.Process("\r\n");

        Assert.Equal(rowAfterImage, CursorViewRow(buffer));
        Assert.Equal(0, buffer.CursorCol);
    }

    // The swallow-the-next-newline flag is scoped to "immediately after the image". A program that
    // emits an image and sends no trailing newline - `cat` of a sixel file is the ordinary case -
    // leaves the flag armed, and it must not then eat a line break belonging to whatever runs next.
    // It did: the flag survived a whole shell prompt (printable text is buffered and flushed through
    // WriteContent, which never cleared it) and swallowed the newline between the next command's
    // echo and its output, rendering `echo done` / `done` as `echo donedone`.
    [Fact]
    public void A_newline_after_other_output_is_not_swallowed()
    {
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal();

        parser.Process("\x1bPq#0;2;0;0;0#0~~~\x1b\\");
        parser.Process("echo done");

        int rowAfterText = CursorViewRow(buffer);
        parser.Process("\r\n");

        Assert.Equal(rowAfterText + 1, CursorViewRow(buffer));
        Assert.Equal(0, buffer.CursorCol);
    }

    // The other half of the same contract, so a fix for the above cannot simply disarm the flag:
    // a newline that really does arrive first is still absorbed, and the image is not followed by a
    // blank line.
    [Fact]
    public void A_newline_arriving_first_is_still_swallowed_after_a_sixel_image()
    {
        (TerminalBuffer buffer, AnsiParser parser) = NewTerminal();

        parser.Process("\x1bPq#0;2;0;0;0#0~~~\x1b\\");
        int rowAfterImage = CursorViewRow(buffer);

        parser.Process("\r\n");

        Assert.Equal(rowAfterImage, CursorViewRow(buffer));
        Assert.Equal(0, buffer.CursorCol);
    }
    private static (TerminalBuffer, AnsiParser) NewTerminal(bool? forceConPtyFiltering = null)
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering) { ImageDecoder = new StubImageDecoder() };
        return (buffer, parser);
    }

    /// <summary>
    /// Any non-empty base64 body. <see cref="StubImageDecoder"/> ignores the bytes and reports a
    /// fixed size, so the payload only has to survive <c>Convert.FromBase64String</c>.
    /// </summary>
    private static string FakePayload() => Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

    private static int CursorViewRow(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return buffer.CursorRow;
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    private static string GraphemeAt(TerminalBuffer buffer, int col, int viewRow)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return buffer.GetGrapheme(col, viewRow);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    private static void AssertCursorBelowImageAtColumnZero(TerminalBuffer buffer, string protocol)
    {
        TerminalImage image = Assert.Single(buffer.Images);

        Assert.Equal(0, buffer.CursorCol);

        // Compared in absolute buffer rows, the same coordinate space AnsiParser stamps onto
        // TerminalImage.CellY, so this stays correct if the placement ever scrolls the viewport.
        int absoluteCursorRow = CursorViewRow(buffer) + (buffer.TotalLines - buffer.Rows);
        Assert.True(
            absoluteCursorRow == image.CellY + image.CellHeight,
            $"{protocol}: expected the cursor on absolute row {image.CellY + image.CellHeight} " +
            $"(immediately below a {image.CellHeight}-row image starting at row {image.CellY}), " +
            $"but it is on row {absoluteCursorRow} at column {buffer.CursorCol}.");
    }

    private sealed class StubImageDecoder : IImageDecoder
    {
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = PixelWidth;
            pixelHeight = PixelHeight;
            return new object();
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = PixelWidth;
            pixelHeight = PixelHeight;
            return new object();
        }
    }
}
