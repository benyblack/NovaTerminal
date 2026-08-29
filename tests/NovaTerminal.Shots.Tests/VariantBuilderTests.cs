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
}
