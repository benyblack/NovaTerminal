using SkiaSharp;

namespace NovaTerminal.Shots;

/// <summary>
/// Composes several already-captured bitmaps into one image. Distinct from
/// <see cref="Rasterizer"/>, which captures a live window: this operates purely on bitmaps
/// already in memory, for scenarios like themes-grid that need several separate captures
/// (one MainWindow per theme, since a theme is only fully applied at construction time)
/// tiled into a single delivered PNG.
/// </summary>
public static class PostProcess
{
    /// <summary>
    /// Lays <paramref name="tiles"/> out left-to-right, top-to-bottom in <paramref name="columns"/>
    /// columns, each cell sized to the largest tile so uneven captures still line up, with
    /// <paramref name="gap"/> pixels of <paramref name="background"/> between and around every
    /// cell (top/left/right/bottom included, not just between neighbours).
    /// </summary>
    public static SKBitmap Grid(IReadOnlyList<SKBitmap> tiles, int columns, int gap, SKColor background)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfZero(tiles.Count);

        int tileWidth = tiles.Max(t => t.Width);
        int tileHeight = tiles.Max(t => t.Height);
        int rows = (tiles.Count + columns - 1) / columns;

        var result = new SKBitmap(
            tileWidth * columns + gap * (columns + 1),
            tileHeight * rows + gap * (rows + 1));

        using var canvas = new SKCanvas(result);
        canvas.Clear(background);

        for (int i = 0; i < tiles.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;

            canvas.DrawBitmap(
                tiles[i],
                gap + column * (tileWidth + gap),
                gap + row * (tileHeight + gap));
        }

        return result;
    }
}
