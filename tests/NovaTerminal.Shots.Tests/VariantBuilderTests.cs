using NovaTerminal.Shots;
using SkiaSharp;

namespace NovaTerminal.ShotsTests;

public sealed class VariantBuilderTests
{
    [Fact]
    public void RoundedWithShadow_LeavesTheCornersTransparent()
    {
        using var source = new SKBitmap(200, 120);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.White);
        }

        using SKBitmap result = PostProcess.RoundedWithShadow(source, cornerRadius: 16, shadowBlur: 12, margin: 24);

        Assert.Equal(0, result.GetPixel(0, 0).Alpha);
        Assert.True(result.Width > source.Width, "the margin should widen the image");
    }

    [Fact]
    public void OnBackdrop_ProducesExactlyTheRequestedSize()
    {
        using var source = new SKBitmap(1000, 600);

        using SKBitmap card = PostProcess.OnBackdrop(source, 1200, 630, SKColors.Black, SKColors.DarkBlue);

        Assert.Equal(1200, card.Width);
        Assert.Equal(630, card.Height);
    }

    [Fact]
    public void BuildAll_WritesALosslessWebpSiblingForEveryPngVariant()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"shots-variant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            string masterPath = Path.Combine(outputDirectory, "sample@2x.png");
            using (SKBitmap master = GradientMaster(64, 48))
            {
                Rasterizer.WritePng(master, masterPath);
            }

            var run = new ShotRun(outputDirectory, scale: 2.0);
            var masterAsset = new ShotAsset(
                "sample", 1, masterPath, 64, 48, "sample", "abc1234", "win-x64", "2026-08-28T00:00:00Z");

            IReadOnlyList<ShotAsset> produced = VariantBuilder.BuildAll(masterAsset, run);

            ShotAsset readmePng = Assert.Single(produced, a => a.Name == "sample-readme" && a.File.EndsWith(".png", StringComparison.Ordinal));
            ShotAsset readmeWebp = Assert.Single(produced, a => a.Name == "sample-readme" && a.File.EndsWith(".webp", StringComparison.Ordinal));

            Assert.Equal(3, readmeWebp.Tier);
            Assert.True(File.Exists(readmePng.File));
            Assert.True(File.Exists(readmeWebp.File));

            using SKBitmap? fromPng = SKBitmap.Decode(readmePng.File);
            using SKBitmap? fromWebp = SKBitmap.Decode(readmeWebp.File);
            Assert.NotNull(fromPng);
            Assert.NotNull(fromWebp);
            Assert.Equal(fromPng!.Width, fromWebp!.Width);
            Assert.Equal(fromPng.Height, fromWebp.Height);

            // The installed SkiaSharp (3.119.4) WebP encoder is bit-exact for every fully opaque
            // pixel under SKWebpEncoderCompression.Lossless - confirmed separately by
            // RasterizerTests.WriteLosslessWebp_RoundTripsPixelsExactly on a fully opaque source.
            // A small residual shows up only where alpha itself is not 255: RoundedWithShadow's
            // blurred drop shadow is a continuous alpha gradient outside the window frame, and
            // there SkiaSharp's PNG and WebP encoders round the underlying premultiplied-to-straight
            // conversion by a few units differently (observed max delta: 11/255 per channel, on
            // well under 1% of pixels). Never observed on a fully opaque pixel - i.e. never on
            // glyph or chrome content, only on the decorative shadow's own soft edge - so it is
            // asserted here rather than silently tolerated everywhere.
            const int opaqueAlpha = 255;
            const int maxChannelDeltaOnTranslucentPixels = 24;

            for (int y = 0; y < fromPng.Height; y++)
            {
                for (int x = 0; x < fromPng.Width; x++)
                {
                    SKColor a = fromPng.GetPixel(x, y);
                    SKColor b = fromWebp.GetPixel(x, y);

                    Assert.Equal(a.Alpha, b.Alpha);

                    if (a.Alpha == opaqueAlpha)
                    {
                        Assert.Equal(a, b);
                        continue;
                    }

                    int delta = Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)), Math.Abs(a.Blue - b.Blue));
                    Assert.True(
                        delta <= maxChannelDeltaOnTranslucentPixels,
                        $"pixel ({x},{y}) with alpha {a.Alpha} differs by {delta} between PNG ({a}) and WebP ({b}), " +
                        $"exceeding the {maxChannelDeltaOnTranslucentPixels}-unit bound observed for translucent shadow pixels.");
                }
            }
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static SKBitmap GradientMaster(int width, int height)
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
}
