using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using SkiaSharp;
using System;
using System.Collections.Concurrent;

namespace NovaTerminal.Tests.RenderTests;

/// <summary>
/// #166 end to end: a pruned image's SKBitmap must survive untouched until a render frame
/// runs (the frame-boundary drain), then be disposed by that drain — and a frame rendered
/// before any prune must not disturb the bitmap it drew.
/// </summary>
public class RetiredImageDisposalTests
{
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8.4f,
        CellHeight = 18.0f,
        Baseline = 14.0f,
        Ascent = 14.0f,
        Descent = 4.0f
    };

    // IsDisposed is protected in SkiaSharp 3.x, and probing a disposed SKBitmap through
    // its public API (GetPixel) dies with a native access violation instead of throwing —
    // so disposal is observed via a subclass flag, never by touching the bitmap.
    private sealed class TrackingBitmap : SKBitmap
    {
        public TrackingBitmap() : base(4, 4) { }
        public bool Gone => IsDisposed;
    }

    [AvaloniaFact]
    public void PrunedImageBitmap_IsDisposedByTheNextFrameDrain_NotBefore()
    {
        var buffer = new TerminalBuffer(80, 24);
        var bitmap = new TrackingBitmap();
        var image = new TerminalImage(bitmap, 2, 2, 2, 1);
        buffer.AddImage(image);

        // Frame 1 draws the image while it is still live in the buffer.
        RenderOneFrame(buffer);
        Assert.False(bitmap.Gone, "drawing a live image must not dispose its bitmap");

        // ED 2 prunes it: removed from the buffer, handle retired but NOT disposed —
        // the just-finished frame may still hold a reference to it.
        buffer.ClearScreen();
        Assert.Empty(buffer.Images);
        Assert.False(bitmap.Gone, "pruning alone must not dispose (previous frame may reference the handle)");

        // Frame 2's drain runs after frame 1's DrawBitmap provably completed: now it disposes.
        RenderOneFrame(buffer);
        Assert.True(bitmap.Gone, "the frame-boundary drain must dispose the retired bitmap");

        // And once disposed, later frames stay clean (nothing re-drains the same handle).
        RenderOneFrame(buffer);
    }

    private static void RenderOneFrame(TerminalBuffer buffer)
    {
        const int cols = 80;
        const int rows = 24;
        int width = (int)Math.Ceiling(cols * Metrics.CellWidth) + 8;
        int height = (int)Math.Ceiling(rows * Metrics.CellHeight);

        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);

        var typeface = new Typeface("Cascadia Code, Consolas, Monospace");
        var glyphTypeface = typeface.GlyphTypeface;
        var skTypeface = new SharedSKTypeface(SKTypeface.FromFamilyName(typeface.FontFamily.Name));
        var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface, 14));

        var op = new TerminalDrawOperation(
            new Rect(0, 0, width, height),
            buffer,
            scrollOffset: 0,
            selection: new SelectionState(),
            searchMatches: null,
            activeSearchIndex: -1,
            metrics: Metrics,
            typeface: typeface,
            fontSize: 14,
            glyphTypeface: glyphTypeface,
            skTypeface: skTypeface,
            skFont: skFont,
            enableLigatures: false,
            fallbackCache: new ConcurrentDictionary<string, SKTypeface?>(),
            fallbackChain: Array.Empty<SKTypeface>(),
            opacity: 1.0,
            hideCursor: true,
            renderScaling: 1.0,
            snapshotRows: buffer.Rows,
            snapshotCols: buffer.Cols,
            totalLines: buffer.TotalLines,
            cursorRow: buffer.CursorRow,
            cursorCol: buffer.CursorCol,
            rowCache: null,
            enableComplexShaping: true,
            glyphCache: null);

        try
        {
            var snapshot = op.DrawTerminalInternal(canvas);
            Assert.NotNull(snapshot);
        }
        finally
        {
            op.Dispose();
            skFont.Dispose();
            skTypeface.Dispose();
        }
    }
}
