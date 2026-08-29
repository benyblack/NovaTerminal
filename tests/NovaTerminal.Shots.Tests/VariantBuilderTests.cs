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
    public void BuildAll_WritesALosslessWebpSiblingWhenItQualifiesAsSmaller()
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
            // conversion by a few units differently (measured max delta on this exact test's
            // deterministic bitmap: 11/255 per channel, on well under 1% of pixels). Never observed
            // on a fully opaque pixel - i.e. never on glyph or chrome content, only on the
            // decorative shadow's own soft edge. The bound below is 12 - the measured 11 plus a
            // single unit of rounding slack, not fresh headroom - so a future SkiaSharp bump that
            // materially changes this residual fails the test rather than sailing through a bound
            // wide enough to hide a regression.
            const int opaqueAlpha = 255;
            const int maxChannelDeltaOnTranslucentPixels = 12;

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

    [Fact]
    public void BuildAll_SkipsTheWebpSiblingWhenItIsNotSmallerThanThePng()
    {
        // og-card and social-square composite onto OnBackdrop's fixed brand-gradient backdrop,
        // and on the real published assets that WebP sibling comes out LARGER than the optimized
        // PNG (measured: og-card +34%, social-square +118%) - the gate in
        // VariantBuilder.WriteNamedVariant exists specifically for this shape of content. This
        // reproduces it with a synthetic "hero-single" master so the skip path has direct
        // coverage rather than relying only on a live harness run to exercise it.
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"shots-variant-card-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            string masterPath = Path.Combine(outputDirectory, "hero-single@2x.png");
            using (SKBitmap master = GradientMaster(1200, 700))
            {
                Rasterizer.WritePng(master, masterPath);
            }

            var run = new ShotRun(outputDirectory, scale: 2.0);
            var masterAsset = new ShotAsset(
                "hero-single", 1, masterPath, 1200, 700, "hero-single", "abc1234", "win-x64", "2026-08-28T00:00:00Z");

            IReadOnlyList<ShotAsset> produced = VariantBuilder.BuildAll(masterAsset, run);

            ShotAsset ogCardPng = Assert.Single(produced, a => a.Name == "og-card");
            ShotAsset socialSquarePng = Assert.Single(produced, a => a.Name == "social-square");

            Assert.EndsWith(".png", ogCardPng.File, StringComparison.Ordinal);
            Assert.EndsWith(".png", socialSquarePng.File, StringComparison.Ordinal);
            Assert.DoesNotContain(produced, a => a.Name == "og-card" && a.File.EndsWith(".webp", StringComparison.Ordinal));
            Assert.DoesNotContain(produced, a => a.Name == "social-square" && a.File.EndsWith(".webp", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(outputDirectory, "og-card.webp")));
            Assert.False(File.Exists(Path.Combine(outputDirectory, "social-square.webp")));

            // README/site, by contrast, are the source's own shape (no fixed-canvas backdrop
            // composite) and DO qualify for this master - confirming the gate is genuinely
            // per-asset rather than accidentally suppressing every WebP for this master.
            Assert.Contains(produced, a => a.Name == "hero-single-readme" && a.File.EndsWith(".webp", StringComparison.Ordinal));
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
