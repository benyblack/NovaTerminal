namespace NovaTerminal.Rendering.Tests;

public class PixelGridTests
{
    private static PixelGrid Build(int cellWidth = 8, int cellHeight = 16)
        => new(
            originXPx: 0,
            originYPx: 0,
            cellWidthPx: cellWidth,
            cellHeightPx: cellHeight,
            baselineOffsetPx: 12,
            underlineOffsetPx: 14,
            strikeOffsetPx: 8);

    [Fact]
    public void XForCol_scales_by_cell_width()
    {
        var grid = Build(cellWidth: 8);

        Assert.Equal(0, grid.XForCol(0));
        Assert.Equal(40, grid.XForCol(5));
    }

    [Fact]
    public void YForBaseline_is_row_top_plus_baseline_offset()
    {
        var grid = Build(cellHeight: 16);

        Assert.Equal(12, grid.YForBaseline(0));
        Assert.Equal(28, grid.YForBaseline(1));
    }

    [Fact]
    public void YForUnderline_is_clamped_to_row_bottom()
    {
        var grid = new PixelGrid(
            originXPx: 0,
            originYPx: 0,
            cellWidthPx: 8,
            cellHeightPx: 16,
            baselineOffsetPx: 12,
            underlineOffsetPx: 20,
            strikeOffsetPx: 8);

        Assert.Equal(16, grid.YForUnderline(0));
    }
}
