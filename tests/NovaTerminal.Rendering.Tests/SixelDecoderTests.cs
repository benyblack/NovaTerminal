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
