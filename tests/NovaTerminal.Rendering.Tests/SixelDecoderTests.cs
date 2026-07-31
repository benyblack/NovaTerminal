using NovaTerminal.Rendering;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Rendering.Tests;

// Regression tests for #169: sixel payloads are remote-controlled input, so
// malformed color parameters must be skipped, not thrown into the parser loop.
public class SixelDecoderTests
{
    // SixelDecoder renders through the SkiaSharp native library. Same convention as
    // GlyphCacheTests: present on Windows CI / dev machines, absent on the Linux
    // gating runner — skip there rather than fail.
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

    [Theory]
    [InlineData(0, 50, 100, 0, 0, 255)]
    [InlineData(120, 50, 100, 255, 0, 0)]
    [InlineData(240, 50, 100, 0, 255, 0)]
    [InlineData(42, 0, 100, 0, 0, 0)]
    [InlineData(42, 100, 100, 255, 255, 255)]
    [InlineData(42, 50, 0, 127, 127, 127)]
    public void HlsToRgb_ReturnsExpectedColor(
        int hue,
        int lightness,
        int saturation,
        byte red,
        byte green,
        byte blue)
    {
        Assert.Equal((red, green, blue), SixelDecoder.HlsToRgb(hue, lightness, saturation));
    }

    [Fact]
    public void HlsToRgb_ClampsOutOfRangeInputs()
    {
        Assert.Equal(SixelDecoder.HlsToRgb(0, 50, 100), SixelDecoder.HlsToRgb(-1, 50, 100));
        Assert.Equal(SixelDecoder.HlsToRgb(360, 50, 100), SixelDecoder.HlsToRgb(361, 50, 100));
        Assert.Equal((byte.MinValue, byte.MinValue, byte.MinValue), SixelDecoder.HlsToRgb(120, -1, 100));
        Assert.Equal((byte.MaxValue, byte.MaxValue, byte.MaxValue), SixelDecoder.HlsToRgb(120, 101, 100));
        Assert.Equal(SixelDecoder.HlsToRgb(120, 50, 0), SixelDecoder.HlsToRgb(120, 50, -1));
        Assert.Equal(SixelDecoder.HlsToRgb(120, 50, 100), SixelDecoder.HlsToRgb(120, 50, 101));
    }

    [Fact]
    public void Decode_HlsRedMatchesRgbRed()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        using var hls = new SixelDecoder().Decode("0;0;0q#1;1;120;50;100#1~");
        using var rgb = new SixelDecoder().Decode("0;0;0q#1;2;100;0;0#1~");

        Assert.NotNull(hls);
        Assert.NotNull(rgb);
        Assert.Equal(rgb.GetPixel(0, 0), hls.GetPixel(0, 0));
    }

    [Theory]
    [InlineData("0;0;0q#1;;2;3;4~~")]          // empty param via consecutive ';'
    [InlineData("0;0;0q#1;2;;3;~~")]           // multiple empties
    [InlineData("0;0;0q#1;2;99999999999;3;4~")] // overflow int.Parse territory
    public void Decode_MalformedColorParams_DoesNotThrow(string dcs)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var decoder = new SixelDecoder();

        var ex = Record.Exception(() => decoder.Decode(dcs));

        Assert.Null(ex);
    }

    [Fact]
    public void Decode_RgbValuesAbove100_AreClampedNotWrapped()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var decoder = new SixelDecoder();

        // type 2 = RGB percentages; 200% would previously wrap the byte cast.
        var ex = Record.Exception(() => decoder.Decode("0;0;0q#1;2;200;200;200#1~~-"));

        Assert.Null(ex);
    }

    [Fact]
    public void Decode_ValidMinimalSixel_ProducesBitmap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var decoder = new SixelDecoder();

        // One 6-pixel column in palette color 1.
        var bitmap = decoder.Decode("0;0;0q#1;2;100;0;0#1~");

        Assert.NotNull(bitmap);
    }
}
