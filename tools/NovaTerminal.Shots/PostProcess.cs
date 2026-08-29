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

    /// <summary>
    /// Pixel-exact crop of <paramref name="source"/> to <paramref name="region"/>, clamped to the
    /// bitmap's own bounds. Uses <see cref="SKBitmap.ExtractSubset"/> rather than drawing onto a
    /// fresh canvas so the result is a direct pixel copy - no resampling, no blending - which
    /// matters for a marketing screenshot's text and hairlines.
    /// </summary>
    public static SKBitmap Crop(SKBitmap source, SKRectI region)
    {
        SKRectI clamped = SKRectI.Intersect(new SKRectI(0, 0, source.Width, source.Height), region);
        if (clamped.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                region,
                $"Crop region does not intersect the {source.Width}x{source.Height} source bitmap.");
        }

        var result = new SKBitmap();
        if (!source.ExtractSubset(result, clamped))
        {
            throw new InvalidOperationException("SKBitmap.ExtractSubset failed to crop the source bitmap.");
        }

        return result;
    }

    /// <summary>
    /// Stacks <paramref name="tiles"/> top-to-bottom, each horizontally centred against the widest
    /// tile, with <paramref name="gap"/> pixels of <paramref name="background"/> between
    /// neighbours (none added above the first or below the last, unlike <see cref="Grid"/> - a
    /// composed single-window frame reads as one continuous surface, not a gallery of separate
    /// tiles). Built for scenarios that capture the same window at two scroll positions and need
    /// the on-topic crop of each stitched into one image, e.g. settings-appearance's theme/preview
    /// section and its font section, ~1450 logical pixels apart in the real scroll.
    /// </summary>
    public static SKBitmap StackVertical(IReadOnlyList<SKBitmap> tiles, int gap, SKColor background)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tiles.Count);

        int width = tiles.Max(t => t.Width);
        int height = tiles.Sum(t => t.Height) + gap * (tiles.Count - 1);

        var result = new SKBitmap(width, height);

        using var canvas = new SKCanvas(result);
        canvas.Clear(background);

        int y = 0;
        foreach (SKBitmap tile in tiles)
        {
            canvas.DrawBitmap(tile, (width - tile.Width) / 2f, y);
            y += tile.Height + gap;
        }

        return result;
    }
}
