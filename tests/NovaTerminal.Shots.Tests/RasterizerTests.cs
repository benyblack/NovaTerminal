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
}
