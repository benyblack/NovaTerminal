using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Shell;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Tests.RenderTests;

/// <summary>
/// Isolates the kitty-image render pipeline: places bitmaps directly into the buffer and
/// renders through the production offscreen renderer. Separates the bitmap source (container
/// decode vs raw RGBA swizzle) and the add path (plain AddImage vs AddKittyFrame) from
/// everything else in the terminal-browser stream.
/// </summary>
public sealed class KittyFrameStreamRenderTests
{
    private static readonly bool SkiaAvailable = CheckSkiaAvailable();

    private static bool CheckSkiaAvailable()
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888));
            return surface != null;
        }
        catch
        {
            return false;
        }
    }

    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 11.333f,
        CellHeight = 24.0f,
        Baseline = 19.0f,
        Ascent = 19.0f,
        Descent = 5.0f
    };

    [Theory]
    [InlineData(21, 8)]
    [InlineData(122, 41)]
    public void OversizedImage_PaintsClipped(int cellW, int cellH)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(86, 30);
        var decoder = new SkiaImageDecoder();
        // 1376x960 raw RGBA like terminal-browser's frames; cell size parameterized so the
        // image can exceed the viewport (122x41 cells on an 86x30 grid).
        var handle = decoder.DecodeRawImage(
            SolidRgba(1376, 960, 0x30, 0x60, 0x90),
            bytesPerPixel: 4, width: 1376, height: 960, out _, out _)!;
        Assert.IsType<SKBitmap>(handle);
        buffer.AddImage(new TerminalImage(handle, 0, 0, cellW, cellH));

        int width = (int)(86 * 11.333f);
        int height = 30 * 24;
        byte[] png = SnapshotService.CapturePng(buffer, Metrics, width, height, new SnapshotCaptureOptions
        {
            HideCursor = true,
        });

        using var rendered = SKBitmap.Decode(png);
        Assert.NotNull(rendered);
        SKColor pixel = rendered.GetPixel(100, 100);
        Assert.True(
            pixel.Red == 0x30 && pixel.Green == 0x60 && pixel.Blue == 0x90,
            $"cells={cellW}x{cellH}: expected frame color at (100,100) but got " +
            $"R={pixel.Red} G={pixel.Green} B={pixel.Blue} A={pixel.Alpha}");
    }

    [Theory]
    [InlineData("container-decode")]
    [InlineData("raw-decode")]
    public void PlacedBitmap_PaintsIntoRenderedPng(string bitmapKind)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(86, 30);
        var decoder = new SkiaImageDecoder();

        int pixelW = 240, pixelH = 100;
        object handle;
        if (bitmapKind == "container-decode")
        {
            var source = new SKBitmap(pixelW, pixelH);
            source.Erase(new SKColor(0x30, 0x60, 0x90, 0xFF));
            using var image = SKImage.FromBitmap(source);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            handle = decoder.DecodeImageBytes(data.ToArray(), out _, out _)!;
        }
        else
        {
            handle = decoder.DecodeRawImage(
                SolidRgba(pixelW, pixelH, 0x30, 0x60, 0x90),
                bytesPerPixel: 4, width: pixelW, height: pixelH, out _, out _)!;
        }

        Assert.IsType<SKBitmap>(handle);
        buffer.AddImage(new TerminalImage(handle, 0, 0, 21, 8));

        int width = (int)(86 * 11.333f);
        int height = 30 * 24;
        byte[] png = SnapshotService.CapturePng(buffer, Metrics, width, height, new SnapshotCaptureOptions
        {
            HideCursor = true,
        });

        using var rendered = SKBitmap.Decode(png);
        Assert.NotNull(rendered);
        SKColor pixel = rendered.GetPixel(100, 100);
        Assert.True(
            pixel.Red == 0x30 && pixel.Green == 0x60 && pixel.Blue == 0x90,
            $"bitmapKind={bitmapKind}: expected frame color at (100,100) but got " +
            $"R={pixel.Red} G={pixel.Green} B={pixel.Blue} A={pixel.Alpha}");
    }

    private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 0xFF;
        }
        return pixels;
    }
}
