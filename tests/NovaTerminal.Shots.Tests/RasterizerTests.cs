using NovaTerminal.Shots;
using SkiaSharp;

namespace NovaTerminal.ShotsTests;

public sealed class RasterizerTests
{
    private static SKBitmap Uniform(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    /// <summary>
    /// A non-uniform bitmap for the optimized-PNG and WebP tests below: a flat colour would
    /// compress to nearly nothing under any encoder, which would not distinguish "this encoder
    /// is lossless" from "there was nothing to lose".
    /// </summary>
    private static SKBitmap Gradient(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(width, height),
            [SKColors.Red, SKColors.Lime, SKColors.Blue],
            null,
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        return bitmap;
    }

    private static void AssertPixelsEqual(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void InkFraction_IsZeroForABlankImage()
    {
        using SKBitmap blank = Uniform(64, 64, SKColors.Black);

        Assert.Equal(0.0, Rasterizer.InkFraction(blank), precision: 6);
    }

    [Fact]
    public void InkFraction_CountsPixelsThatDifferFromTheDominantColour()
    {
        using SKBitmap bitmap = Uniform(10, 10, SKColors.Black);
        for (int x = 0; x < 10; x++)
        {
            bitmap.SetPixel(x, 0, SKColors.White);
        }

        Assert.Equal(0.10, Rasterizer.InkFraction(bitmap), precision: 6);
    }

    [Fact]
    public void WritePng_CreatesADecodableFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shots-{Guid.NewGuid():N}.png");
        using SKBitmap bitmap = Uniform(8, 8, SKColors.Red);

        try
        {
            Rasterizer.WritePng(bitmap, path);

            using SKBitmap? decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(8, decoded!.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InkFraction_ThrowsOnAZeroPixelBitmap()
    {
        using var bitmap = new SKBitmap(0, 0);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => Rasterizer.InkFraction(bitmap));
        Assert.Equal("bitmap", ex.ParamName);
    }

    [Fact]
    public void WriteOptimizedPng_RoundTripsPixelsExactly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shots-opt-{Guid.NewGuid():N}.png");
        using SKBitmap bitmap = Gradient(48, 32);

        try
        {
            Rasterizer.WriteOptimizedPng(bitmap, path);

            using SKBitmap? decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            AssertPixelsEqual(bitmap, decoded!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteOptimizedPng_IsNoLargerThanTheUnoptimizedDefault()
    {
        // AllFilters + zlib level 9 should never lose to Skia's own zero-tuning default
        // (bitmap.Encode(Png, 100)) - if it ever does, the optimisation pass is pointless.
        string defaultPath = Path.Combine(Path.GetTempPath(), $"shots-default-{Guid.NewGuid():N}.png");
        string optimizedPath = Path.Combine(Path.GetTempPath(), $"shots-optimized-{Guid.NewGuid():N}.png");
        using SKBitmap bitmap = Gradient(256, 256);

        try
        {
            Rasterizer.WritePng(bitmap, defaultPath);
            Rasterizer.WriteOptimizedPng(bitmap, optimizedPath);

            var defaultSize = new FileInfo(defaultPath).Length;
            var optimizedSize = new FileInfo(optimizedPath).Length;
            Assert.True(
                optimizedSize <= defaultSize,
                $"optimized PNG ({optimizedSize} bytes) was larger than the default encode ({defaultSize} bytes).");
        }
        finally
        {
            File.Delete(defaultPath);
            File.Delete(optimizedPath);
        }
    }

    [Fact]
    public void WriteLosslessWebp_RoundTripsPixelsExactly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shots-lossless-{Guid.NewGuid():N}.webp");
        using SKBitmap bitmap = Gradient(48, 32);

        try
        {
            Rasterizer.WriteLosslessWebp(bitmap, path);

            using SKBitmap? decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            AssertPixelsEqual(bitmap, decoded!);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
