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
    /// neighbours (none added above the first or below the last, unlike <see cref="Grid"/>, which
    /// also borders the outside). Built for scenarios that capture the same window at two scroll
    /// positions and need the on-topic crop of each stitched into one image, e.g.
    /// settings-appearance's theme/preview section and its font section, ~1450 logical pixels apart
    /// in the real scroll, with the Title Bar and Window sections elided from between them. Passing
    /// a nonzero <paramref name="gap"/> with a <paramref name="background"/> that contrasts with the
    /// tiles' own chrome (the convention <see cref="Grid"/> already established for themes-grid) is
    /// deliberate here: a splice that elides real content between its two captures must read as a
    /// join, not as an unbroken continuation of one scroll position.
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

    /// <summary>
    /// Rounds the corners and drops a shadow. The headless renderer cannot produce the OS
    /// window shadow or rounded corners, so they are added here; without them a capture reads
    /// as a flat rectangle pasted onto a page rather than a window.
    /// </summary>
    public static SKBitmap RoundedWithShadow(SKBitmap source, float cornerRadius, float shadowBlur, int margin)
    {
        var result = new SKBitmap(source.Width + margin * 2, source.Height + margin * 2);

        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        var bounds = new SKRect(margin, margin, margin + source.Width, margin + source.Height);
        var rounded = new SKRoundRect(bounds, cornerRadius, cornerRadius);

        using (var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 140),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(shadowBlur, shadowBlur)
        })
        {
            canvas.DrawRoundRect(rounded, shadowPaint);
        }

        canvas.Save();
        canvas.ClipRoundRect(rounded, antialias: true);
        canvas.DrawBitmap(source, bounds.Left, bounds.Top);
        canvas.Restore();

        return result;
    }

    /// <summary>
    /// Composites <paramref name="source"/> onto a fixed <paramref name="width"/> x
    /// <paramref name="height"/> canvas filled with a top-to-bottom gradient from
    /// <paramref name="top"/> to <paramref name="bottom"/>, scaled to fill 86% of the canvas
    /// on whichever axis is tighter and centred on the other. Built for card-shaped exports
    /// (an OG card, a social square) whose dimensions are fixed by the platform they target,
    /// unlike <see cref="RoundedWithShadow"/>'s output, which keeps the source's own aspect
    /// ratio.
    /// </summary>
    public static SKBitmap OnBackdrop(SKBitmap source, int width, int height, SKColor top, SKColor bottom)
    {
        var result = new SKBitmap(width, height);

        using var canvas = new SKCanvas(result);
        using (var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                [top, bottom],
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(new SKRect(0, 0, width, height), background);
        }

        float scale = Math.Min((width * 0.86f) / source.Width, (height * 0.86f) / source.Height);
        float drawWidth = source.Width * scale;
        float drawHeight = source.Height * scale;

        canvas.DrawBitmap(
            source,
            new SKRect(
                (width - drawWidth) / 2,
                (height - drawHeight) / 2,
                (width + drawWidth) / 2,
                (height + drawHeight) / 2));

        return result;
    }
}
