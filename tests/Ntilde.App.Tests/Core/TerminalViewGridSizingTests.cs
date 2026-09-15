using Avalonia;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// <see cref="TerminalView.TryComputeGrid"/> must refuse a size it cannot express as a cell grid,
/// rather than clamping one into existence.
/// </summary>
/// <remarks>
/// <para>
/// The clamp it replaced - <c>Math.Max(cols, 1)</c>, <c>Math.Max(rows, 1)</c> - turned a control
/// with no size into a 1x1 terminal, and a 1x1 resize is destructive rather than merely useless.
/// At one column the buffer re-wraps the whole transcript to one character per row, which blows the
/// scrollback budget; the eviction that follows is permanent, so growing back cannot undo it.
/// Measured on <c>TerminalBuffer</c> directly, a plain resize against a 1x1 round trip: of 2000
/// lines, 2000 survive the first and 80 the second; of 200, all 200 against 72.
/// </para>
/// <para>
/// Zero size is ordinary. A control that has not been laid out, one inside a collapsed or hidden
/// container, and one detached from the visual tree all measure zero, and every one of those used
/// to be reported to the buffer - and forwarded to the PTY - as a one-by-one terminal.
/// </para>
/// <para>
/// Tested through the seam rather than by driving layout, because what needs pinning is the
/// decision itself: which sizes are refused, and that the accepted ones are unchanged.
/// </para>
/// </remarks>
public class TerminalViewGridSizingTests
{
    private const double CellWidth = 8;
    private const double CellHeight = 16;

    [Theory]
    // No size at all: the case the clamp turned into 1x1.
    [InlineData(0, 0)]
    [InlineData(0, 800)]
    [InlineData(1200, 0)]
    // Narrower than the 4px padding, so no width is left for even one column.
    [InlineData(4, 800)]
    [InlineData(3, 800)]
    // Wide enough for padding but not for a whole cell, and tall enough but not for a whole row.
    [InlineData(11, 800)]
    [InlineData(1200, 15)]
    // The degenerate doubles a layout pass can hand over. These are here because the guard relies
    // on them falling out of the same comparison rather than being checked for by name, which is
    // a claim worth pinning rather than trusting.
    [InlineData(double.NaN, 800)]
    [InlineData(1200, double.NaN)]
    [InlineData(double.PositiveInfinity, 800)]
    [InlineData(1200, double.PositiveInfinity)]
    [InlineData(-1200, -800)]
    public void RefusesASizeThatCannotHoldACell(double width, double height)
    {
        bool ok = TerminalView.TryComputeGrid(new Size(width, height), CellWidth, CellHeight, out int cols, out int rows);

        Assert.False(ok, $"{width}x{height} should be refused, got {cols}x{rows}");
        Assert.Equal(0, cols);
        Assert.Equal(0, rows);
    }

    /// <summary>Unmeasured text metrics are refused too; they would divide by zero or worse.</summary>
    [Theory]
    [InlineData(0, 16)]
    [InlineData(8, 0)]
    [InlineData(-1, 16)]
    public void RefusesUnmeasuredCellMetrics(double cellWidth, double cellHeight)
    {
        Assert.False(TerminalView.TryComputeGrid(new Size(1200, 800), cellWidth, cellHeight, out _, out _));
    }

    /// <summary>
    /// The accepted cases still compute exactly what they did before, padding included: a real
    /// resize must not have been narrowed by the guard.
    /// </summary>
    [Theory]
    [InlineData(804, 800, 100, 50)]
    [InlineData(1200, 800, 149, 50)]
    [InlineData(12, 16, 1, 1)]     // the smallest size that genuinely holds one cell
    [InlineData(1200, 807, 149, 50)] // a partial row is dropped, not rounded up
    public void AcceptsARealSizeAndComputesTheSameGrid(double width, double height, int expectedCols, int expectedRows)
    {
        bool ok = TerminalView.TryComputeGrid(new Size(width, height), CellWidth, CellHeight, out int cols, out int rows);

        Assert.True(ok, $"{width}x{height} should be accepted");
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedRows, rows);
    }
}
