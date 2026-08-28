using NovaTerminal.Shots;
using SkiaSharp;

namespace NovaTerminal.ShotsTests;

public sealed class PostProcessGridTests
{
    [Fact]
    public void Grid_LaysTilesOutInRowsWithGaps()
    {
        var tiles = Enumerable.Range(0, 5).Select(_ => new SKBitmap(100, 50)).ToList();

        using SKBitmap grid = PostProcess.Grid(tiles, columns: 2, gap: 10, background: SKColors.Black);

        // 2 columns -> 3 rows for 5 tiles.
        Assert.Equal(100 * 2 + 10 * 3, grid.Width);
        Assert.Equal(50 * 3 + 10 * 4, grid.Height);

        foreach (SKBitmap tile in tiles)
        {
            tile.Dispose();
        }
    }

    [Fact]
    public void Grid_PlacesEachTileAtItsOwnCellRatherThanOverlapping()
    {
        using SKBitmap red = Solid(20, 20, SKColors.Red);
        using SKBitmap green = Solid(20, 20, SKColors.Green);
        using SKBitmap blue = Solid(20, 20, SKColors.Blue);

        using SKBitmap grid = PostProcess.Grid([red, green, blue], columns: 2, gap: 5, background: SKColors.Black);

        // Row 0: red at column 0, green at column 1. Row 1: blue at column 0, background at column 1.
        Assert.Equal(SKColors.Red, grid.GetPixel(5 + 10, 5 + 10));
        Assert.Equal(SKColors.Green, grid.GetPixel(5 + 20 + 5 + 10, 5 + 10));
        Assert.Equal(SKColors.Blue, grid.GetPixel(5 + 10, 5 + 20 + 5 + 10));
        Assert.Equal(SKColors.Black, grid.GetPixel(5 + 20 + 5 + 10, 5 + 20 + 5 + 10));
    }

    [Fact]
    public void Grid_ThrowsForFewerThanOneColumn()
    {
        using SKBitmap tile = new(10, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => PostProcess.Grid([tile], columns: 0, gap: 0, background: SKColors.Black));
    }

    [Fact]
    public void Grid_ThrowsForNoTiles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostProcess.Grid([], columns: 2, gap: 0, background: SKColors.Black));
    }

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }
}
